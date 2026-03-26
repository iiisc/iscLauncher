using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using iscLauncher.Models;

namespace iscLauncher.Services;

public record GitResult(bool Success, string Output);
public record GitCommitEntry(string Hash, string Message, string Body = "", string DateString = "");

public class GitService
{
    private static readonly string SyncCacheRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "IscLauncher", "SyncCache");

    public string GetLocalCachePath(GameEntry game) =>
        Path.Combine(SyncCacheRoot, game.Id.ToString());

    public async Task<bool> IsGitInstalledAsync(CancellationToken ct = default)
    {
        var result = await RunGitAsync("--version", workingDir: null, ct);
        return result.Success;
    }

    public async Task<GitResult> CloneAsync(string repoUrl, string branch, string targetDir, CancellationToken ct = default, IProgress<string>? progress = null)
    {
        var branchArg = string.IsNullOrWhiteSpace(branch) ? "main" : branch;
        var result = await RunGitAsync($"clone --progress --branch \"{branchArg}\" \"{repoUrl}\" \"{targetDir}\"", workingDir: null, ct, progress);
        if (result.Success)
            return result;

        // Empty repos have no branches — clone without --branch
        if (Directory.Exists(targetDir))
            Directory.Delete(targetDir, true);
        return await RunGitAsync($"clone --progress \"{repoUrl}\" \"{targetDir}\"", workingDir: null, ct, progress);
    }

    public async Task<GitResult> PullAsync(string repoDir, CancellationToken ct = default) =>
        await RunGitAsync($"-C \"{repoDir}\" pull", workingDir: null, ct);

    public async Task<GitResult> AddAllAsync(string repoDir, CancellationToken ct = default) =>
        await RunGitAsync($"-C \"{repoDir}\" add -A", workingDir: null, ct);

    public async Task<GitResult> CommitAsync(string repoDir, string message, CancellationToken ct = default)
    {
        var msgFile = Path.Combine(repoDir, ".git", "COMMIT_MSG_TMP");
        try
        {
            await File.WriteAllTextAsync(msgFile, message, new System.Text.UTF8Encoding(false), ct);
            return await RunGitAsync(
                $"-C \"{repoDir}\" -c user.name=\"iscLauncher\" -c user.email=\"noreply\" -c i18n.commitEncoding=utf-8 commit --file=\"{msgFile}\"",
                workingDir: null, ct);
        }
        finally
        {
            try { File.Delete(msgFile); } catch { }
        }
    }

    public async Task<GitResult> PushAsync(string repoDir, CancellationToken ct = default) =>
        await RunGitAsync($"-C \"{repoDir}\" push -u origin HEAD", workingDir: null, ct);

    public async Task<string?> GetRemoteUrlAsync(string repoDir, CancellationToken ct = default)
    {
        var result = await RunGitAsync($"-C \"{repoDir}\" remote get-url origin", workingDir: null, ct);
        return result.Success ? result.Output.Trim() : null;
    }

    public async Task<bool> IsStatusCleanAsync(string repoDir, CancellationToken ct = default)
    {
        var result = await RunGitAsync($"-C \"{repoDir}\" status --porcelain", workingDir: null, ct);
        return result.Success && string.IsNullOrWhiteSpace(result.Output);
    }

    public async Task<List<GitCommitEntry>> LogAsync(string repoDir, int count = 20, CancellationToken ct = default)
    {
        // Use record separator (ASCII 30) to delimit entries and unit separator (ASCII 31) between fields
        const char rs = '\x1e';
        const char us = '\x1f';
        var format = $"--format={rs}%h{us}%s{us}%b{us}%ai";
        var result = await RunGitAsync($"-C \"{repoDir}\" -c i18n.logOutputEncoding=utf-8 log {format} -n {count}", workingDir: null, ct);
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

    public async Task<GitResult> CheckoutCommitFilesAsync(string repoDir, string commitHash, CancellationToken ct = default) =>
        await RunGitAsync($"-C \"{repoDir}\" checkout {commitHash} -- .", workingDir: null, ct);

    private static async Task<GitResult> RunGitAsync(string arguments, string? workingDir, CancellationToken ct, IProgress<string>? progress = null)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };

            if (!string.IsNullOrEmpty(workingDir))
                psi.WorkingDirectory = workingDir;

            using var process = new Process { StartInfo = psi };

            var stderrBuilder = new System.Text.StringBuilder();

            if (progress != null)
            {
                process.ErrorDataReceived += (_, args) =>
                {
                    if (!string.IsNullOrWhiteSpace(args.Data))
                    {
                        stderrBuilder.AppendLine(args.Data);
                        progress.Report(args.Data);
                    }
                };
            }

            process.Start();

            if (progress != null)
                process.BeginErrorReadLine();

            var outputTask = process.StandardOutput.ReadToEndAsync(ct);

            // Only read stderr synchronously if we're not streaming it
            string stderr;
            if (progress != null)
            {
                await process.WaitForExitAsync(ct);
                stderr = stderrBuilder.ToString();
            }
            else
            {
                var errorTask = process.StandardError.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct);
                stderr = await errorTask;
            }

            var stdout = await outputTask;

            if (process.ExitCode == 0)
                return new GitResult(true, stdout);

            return new GitResult(false, string.IsNullOrWhiteSpace(stderr) ? stdout : stderr);
        }
        catch (OperationCanceledException)
        {
            return new GitResult(false, "Operation cancelled.");
        }
        catch (Exception ex)
        {
            return new GitResult(false, ex.Message);
        }
    }
}
