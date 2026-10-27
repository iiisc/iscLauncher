using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using iscLauncher.Models;

namespace iscLauncher.Services;

public record GitResult(bool Success, string Output);

public record GitCommitEntry(string Hash, string Message, string Body, string DateString)
{
    public string DisplayDate => DateTimeOffset.TryParse(DateString, out var date)
        ? date.LocalDateTime.ToString("yyyy-MM-dd HH:mm")
        : DateString;
}

public class GitService
{
    private static readonly string SyncCacheRoot = Path.Combine(GameRepository.LocalDataFolder, "SyncCache");

    /// <summary>Where the Git that the app downloads for a PC without one is kept.</summary>
    public static readonly string DownloadedGitDir = Path.Combine(GameRepository.LocalDataFolder, "Git");

    // Git for Windows' portable build, made for apps to carry. Pinned and checked, since the app runs it; the Update
    // MinGit workflow proposes a new pin when there's a newer one.
    private const string MinGitUrl = "https://github.com/git-for-windows/git/releases/download/v2.56.0.windows.4/MinGit-2.56.0.4-64-bit.zip";
    private const string MinGitSha256 = "e3bf78351fe777fba47ddee5950082f8a98bb0f02604fdc7ba3ee2cb68ffa877";
    public const string MinGitName = "MinGit 2.56";
    public const int MinGitDownloadMB = 38;

    // An installed Git goes first: its credential manager can still sign in to repos the app's GitHub login doesn't cover
    private static readonly string[] Candidates = ["git", Path.Combine(DownloadedGitDir, "cmd", "git.exe")];

    private readonly Func<string?> _gitHubToken;

    // Of git's output only the transfer percentage means anything to the user
    private static readonly Regex ProgressLine = new(@"^Receiving objects:\s+(\d+)%");

    // Only a successful lookup is kept, so installing Git mid-session is picked up on the next check
    private string? _git;

    public GitService(Func<string?> gitHubToken) => _gitHubToken = gitHubToken;

    public string GetLocalCachePath(GameEntry game) =>
        Path.Combine(SyncCacheRoot, game.Id.ToString());

    public async Task<bool> IsGitInstalledAsync(CancellationToken ct)
    {
        if (_git != null)
            return true;
        foreach (var candidate in Candidates)
        {
            if ((await RunGitAsync(["--version"], ct, git: candidate)).Success)
            {
                _git = candidate;
                return true;
            }
        }
        return false;
    }

    /// <summary>Downloads <see cref="MinGitName"/> into <see cref="DownloadedGitDir"/>, for a PC that has no Git.</summary>
    public async Task DownloadGitAsync(IProgress<double> progress, CancellationToken ct)
    {
        var zip = Path.Combine(Path.GetTempPath(), "iscLauncher-MinGit.zip");
        try
        {
            await AppUpdateService.DownloadAsync(MinGitUrl, zip, progress, ct);
            await InstallZipAsync(zip, MinGitSha256, DownloadedGitDir, ct);
        }
        finally
        {
            try { File.Delete(zip); } catch (IOException) { }
        }
        if (!await IsGitInstalledAsync(ct))
            throw new AppException("Git was downloaded but won't start. Try again, or install Git from git-scm.com.");
    }

    /// <summary>Unpacks a checked download into <paramref name="targetDir"/>, replacing what's there.</summary>
    internal static async Task InstallZipAsync(string zip, string sha256, string targetDir, CancellationToken ct)
    {
        await AppUpdateService.VerifyAsync(zip, sha256, ct);
        var partial = targetDir + ".partial";
        if (Directory.Exists(partial))
            Directory.Delete(partial, true);
        await Task.Run(() => ZipFile.ExtractToDirectory(zip, partial), ct);
        // Moved into place only once whole, so a cancelled or failed unpack never passes for an installed Git
        if (Directory.Exists(targetDir))
            Directory.Delete(targetDir, true);
        Directory.Move(partial, targetDir);
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
                ["config", "core.autocrlf", "false"],
                // Only the current files are downloaded; git fetches older versions when Rollback asks for them, so a
                // repo whose history grows with every push stays quick to open
                ["config", "remote.origin.promisor", "true"],
                ["config", "remote.origin.partialclonefilter", "blob:none"]);
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

    /// <summary>The remote branch's newest commit, or null if there is none or it can't be reached. Never asks for a login.</summary>
    public async Task<string?> GetRemoteHeadAsync(string repoUrl, string branch, CancellationToken ct)
    {
        var result = await RunGitAsync(["ls-remote", repoUrl, $"refs/heads/{branch}"], ct, quiet: true);
        var hash = result.Success ? result.Output.Split('\t', 2)[0].Trim() : "";
        return hash.Length > 0 ? hash : null;
    }

    /// <summary>The checked-out commit, or null on a branch with no commits yet.</summary>
    public async Task<string?> GetHeadAsync(string repoDir, CancellationToken ct)
    {
        var result = await RunGitAsync(["-C", repoDir, "rev-parse", "-q", "--verify", "HEAD"], ct);
        return result.Success ? result.Output.Trim() : null;
    }

    /// <summary>Replaces the working tree and index with exactly the files of <paramref name="commit"/>, including deletions.</summary>
    public async Task<GitResult> RestoreTreeAsync(string repoDir, string commit, CancellationToken ct) =>
        await RunGitAsync(["-C", repoDir, "read-tree", "-u", "--reset", commit], ct);

    public async Task<GitResult> AddAllAsync(string repoDir, CancellationToken ct) =>
        await RunGitAsync(["-C", repoDir, "add", "-A"], ct);

    public async Task<GitResult> CommitAsync(string repoDir, string message, CancellationToken ct)
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

    public async Task<GitResult> PushAsync(string repoDir, string branch, CancellationToken ct) =>
        await RunGitAsync(["-C", repoDir, "push", "origin", $"HEAD:refs/heads/{branch}"], ct);

    public async Task<bool> IsStatusCleanAsync(string repoDir, CancellationToken ct)
    {
        var result = await RunGitAsync(["-C", repoDir, "status", "--porcelain"], ct);
        return result.Success && string.IsNullOrWhiteSpace(result.Output);
    }

    public async Task<List<GitCommitEntry>> LogAsync(string repoDir, int count, CancellationToken ct)
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

    private async Task<bool> IsMirrorOfAsync(string repoDir, string repoUrl, CancellationToken ct)
    {
        if (!Directory.Exists(Path.Combine(repoDir, ".git")))
            return false;

        var url = await RunGitAsync(["-C", repoDir, "remote", "get-url", "origin"], ct);
        return url.Success && string.Equals(url.Output.Trim().TrimEnd('/'), repoUrl.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    private async Task<GitResult> RunStepsAsync(string repoDir, CancellationToken ct, params string[][] steps)
    {
        foreach (var step in steps)
        {
            var result = await RunGitAsync(["-C", repoDir, .. step], ct);
            if (!result.Success)
                return result;
        }
        return new GitResult(true, "");
    }

    /// <param name="quiet">For background checks: fail rather than show a login prompt.</param>
    private async Task<GitResult> RunGitAsync(string[] arguments, CancellationToken ct, IProgress<string>? progress = null, string? git = null, bool quiet = false)
    {
        git ??= _git ?? "git";
        var psi = new ProcessStartInfo
        {
            FileName = git,
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

        // Signed in through the app: git gets the token through its environment, where it's neither saved in the repo's
        // config nor visible in the process list
        if (_gitHubToken() is { } token)
        {
            psi.Environment["GIT_CONFIG_COUNT"] = "1";
            psi.Environment["GIT_CONFIG_KEY_0"] = "http.https://github.com/.extraheader";
            psi.Environment["GIT_CONFIG_VALUE_0"] = "AUTHORIZATION: basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{token}"));
        }
        // The portable Git has no credential manager, so without a token it would wait on a login prompt nobody can see
        if (git != "git" || quiet)
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (quiet)
            psi.Environment["GCM_INTERACTIVE"] = "never";

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
                if (progress != null && ProgressLine.Match(args.Data) is { Success: true } line)
                    progress.Report($"Downloading from the repo... {line.Groups[1].Value}%");
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
