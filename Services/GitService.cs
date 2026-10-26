using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using iscLauncher.Models;

namespace iscLauncher.Services;

public record GitResult(bool Success, string Output);

public record GitCommitEntry(string Hash, string Message, string Body = "", string DateString = "")
{
    public string DisplayDate => DateTimeOffset.TryParse(DateString, out var date)
        ? date.LocalDateTime.ToString("yyyy-MM-dd HH:mm")
        : DateString;
}

public class GitService
{
    private static readonly string SyncCacheRoot = Path.Combine(GameRepository.AppDataFolder, "SyncCache");

    // Only a positive result is cached, so installing git mid-session is picked up on the next check
    private static bool _gitFound;

    public string GetLocalCachePath(GameEntry game) =>
        Path.Combine(SyncCacheRoot, game.Id.ToString());

    public async Task<bool> IsGitInstalledAsync(CancellationToken ct = default)
    {
        if (!_gitFound)
            _gitFound = (await RunGitAsync(["--version"], ct)).Success;
        return _gitFound;
    }

    /// <summary>
    /// Makes <paramref name="repoDir"/> an exact copy of origin/<paramref name="branch"/>, discarding local commits or edits
    /// left by an earlier failed or cancelled operation. A branch the remote doesn't have yet becomes an empty unborn
    /// branch, so the first push creates it.
    /// </summary>
    public async Task<GitResult> MirrorAsync(string repoUrl, string branch, string repoDir, IProgress<string>? progress, CancellationToken ct)
    {
        if (!await IsMirrorOfAsync(repoDir, repoUrl, ct))
        {
            if (Directory.Exists(repoDir))
                await Task.Run(() => AddonFiles.ForceDeleteDirectory(repoDir), ct);
            Directory.CreateDirectory(repoDir);
            var init = await RunStepsAsync(repoDir, ct,
                ["init", "-q"],
                ["remote", "add", "origin", repoUrl],
                // Addon files must stay byte-identical to the game's copies, whatever the user's global setting is
                ["config", "core.autocrlf", "false"]);
            if (!init.Success)
                return init;
        }

        var fetch = await RunGitAsync(["-C", repoDir, "fetch", "--prune", "--progress", "origin"], ct, progress);
        if (!fetch.Success)
            return fetch;

        var remoteBranch = $"refs/remotes/origin/{branch}";
        if ((await RunGitAsync(["-C", repoDir, "rev-parse", "--verify", "-q", remoteBranch], ct)).Success)
            return await RunStepsAsync(repoDir, ct, ["checkout", "-q", "-f", "-B", branch, remoteBranch], ["clean", "-fdxq"]);

        return await RunStepsAsync(repoDir, ct,
            ["symbolic-ref", "HEAD", $"refs/heads/{branch}"],
            ["update-ref", "-d", $"refs/heads/{branch}"],
            ["read-tree", "--empty"],
            ["clean", "-fdxq"]);
    }

    /// <summary>The checked-out commit, or null on a branch with no commits yet.</summary>
    public async Task<string?> GetHeadAsync(string repoDir, CancellationToken ct = default)
    {
        var result = await RunGitAsync(["-C", repoDir, "rev-parse", "-q", "--verify", "HEAD"], ct);
        return result.Success ? result.Output.Trim() : null;
    }

    /// <summary>Replaces the working tree and index with exactly the files of <paramref name="commit"/>, including deletions.</summary>
    public async Task<GitResult> RestoreTreeAsync(string repoDir, string commit, CancellationToken ct = default) =>
        await RunGitAsync(["-C", repoDir, "read-tree", "-u", "--reset", commit], ct);

    public async Task<GitResult> AddAllAsync(string repoDir, CancellationToken ct = default) =>
        await RunGitAsync(["-C", repoDir, "add", "-A"], ct);

    public async Task<GitResult> CommitAsync(string repoDir, string message, CancellationToken ct = default)
    {
        var msgFile = Path.Combine(repoDir, ".git", "COMMIT_MSG_TMP");
        try
        {
            await File.WriteAllTextAsync(msgFile, message, new UTF8Encoding(false), ct);
            return await RunGitAsync(
                ["-C", repoDir, "-c", "user.name=iscLauncher", "-c", "user.email=noreply", "-c", "i18n.commitEncoding=utf-8",
                 "commit", $"--file={msgFile}"], ct);
        }
        finally
        {
            try { File.Delete(msgFile); } catch { }
        }
    }

    public async Task<GitResult> PushAsync(string repoDir, string branch, CancellationToken ct = default) =>
        await RunGitAsync(["-C", repoDir, "push", "origin", $"HEAD:refs/heads/{branch}"], ct);

    public async Task<bool> IsStatusCleanAsync(string repoDir, CancellationToken ct = default)
    {
        var result = await RunGitAsync(["-C", repoDir, "status", "--porcelain"], ct);
        return result.Success && string.IsNullOrWhiteSpace(result.Output);
    }

    public async Task<List<GitCommitEntry>> LogAsync(string repoDir, int count = 20, CancellationToken ct = default)
    {
        // Use record separator (ASCII 30) to delimit entries and unit separator (ASCII 31) between fields
        const char rs = '\x1e';
        const char us = '\x1f';
        var format = $"--format={rs}%h{us}%s{us}%b{us}%ai";
        var result = await RunGitAsync(["-C", repoDir, "-c", "i18n.logOutputEncoding=utf-8", "log", format, "-n", count.ToString()], ct);
        var entries = new List<GitCommitEntry>();
        if (!result.Success)
            return entries;

        foreach (var record in result.Output.Split(rs, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = record.Split(us);
            if (parts.Length >= 2)
            {
                var hash = parts[0].Trim();
                var subject = parts[1].Trim();
                var body = parts.Length >= 3 ? parts[2].Trim() : "";
                var dateStr = parts.Length >= 4 ? parts[3].Trim() : "";
                if (!string.IsNullOrEmpty(hash))
                    entries.Add(new GitCommitEntry(hash, subject, body, dateStr));
            }
        }
        return entries;
    }

    private static async Task<bool> IsMirrorOfAsync(string repoDir, string repoUrl, CancellationToken ct)
    {
        if (!Directory.Exists(Path.Combine(repoDir, ".git")))
            return false;

        var url = await RunGitAsync(["-C", repoDir, "remote", "get-url", "origin"], ct);
        var autocrlf = await RunGitAsync(["-C", repoDir, "config", "--get", "core.autocrlf"], ct);
        // Caches cloned by older versions converted line endings, so they're rebuilt once
        return url.Success
            && string.Equals(url.Output.Trim().TrimEnd('/'), repoUrl.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
            && autocrlf.Output.Trim() == "false";
    }

    private static async Task<GitResult> RunStepsAsync(string repoDir, CancellationToken ct, params string[][] steps)
    {
        foreach (var step in steps)
        {
            var result = await RunGitAsync(["-C", repoDir, .. step], ct);
            if (!result.Success)
                return result;
        }
        return new GitResult(true, "");
    }

    private static async Task<GitResult> RunGitAsync(string[] arguments, CancellationToken ct, IProgress<string>? progress = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        // ArgumentList quotes each value, so paths and URLs with spaces or quotes stay intact
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        try
        {
            // git writes progress and errors to stderr, line by line
            var stderr = new StringBuilder();
            process.ErrorDataReceived += (_, args) =>
            {
                if (string.IsNullOrWhiteSpace(args.Data))
                    return;
                lock (stderr)
                    stderr.AppendLine(args.Data);
                progress?.Report(args.Data);
            };

            process.Start();
            process.BeginErrorReadLine();
            var stdout = await process.StandardOutput.ReadToEndAsync(ct);
            // Also waits for the last stderr line to be handled
            await process.WaitForExitAsync(ct);

            return process.ExitCode == 0
                ? new GitResult(true, stdout)
                : new GitResult(false, stderr.Length == 0 ? stdout : stderr.ToString());
        }
        catch (OperationCanceledException)
        {
            // Left running, git would keep writing into the cache or finish a push the user cancelled
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        catch (Exception ex)
        {
            return new GitResult(false, ex.Message);
        }
    }
}
