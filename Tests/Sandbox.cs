using System;
using System.Diagnostics;
using System.IO;
using iscLauncher.Models;
using iscLauncher.Services;

namespace iscLauncher.Tests;

/// <summary>
/// A temp folder holding fake game installs, a bare git remote and a library file of their own, so tests never touch
/// the real games.json. Sync caches and backups still go under the app's data folder, and are removed per game afterwards.
/// </summary>
public sealed class Sandbox : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "isc-tests-" + Guid.NewGuid().ToString("N")[..8]);
    public GameRepository Library { get; }
    public AddonSyncService Sync { get; }

    public Sandbox()
    {
        Directory.CreateDirectory(Root);
        Library = new GameRepository(Path.Combine(Root, "games.json"));
        Sync = new AddonSyncService(new GitService(() => null), Library);
    }

    public string NewRemote()
    {
        var remote = Path.Combine(Root, "remote.git");
        Git(Root, "init", "-q", "--bare", "-b", "main", remote);
        return remote;
    }

    public GameEntry AddGame(string name, string? remote = null)
    {
        var dir = Path.Combine(Root, name);
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "Wow.exe");
        // Any exe with a version resource does; git's major version is 2, so this plays a TBC client
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Git\cmd\git.exe"), exe);
        var game = new GameEntry { Name = name, ExecutablePath = exe, SyncRepoUrl = remote, AccountName = "acc" };
        Library.Games.Add(game);
        return game;
    }

    public static string Dir(GameEntry game) => game.GameDir;

    public static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    /// <summary>An addon made for a 3.3 client unless <paramref name="iface"/> says otherwise.</summary>
    public static void Addon(string gameDir, string name, string version, int iface = 30300, string body = "")
    {
        var dir = Path.Combine(gameDir, "Interface", "AddOns", name);
        Write(Path.Combine(dir, name + ".toc"), $"## Interface: {iface}\n## Title: |cff00ff00{name}|r\n## Version: {version}\n{name}.lua\n");
        Write(Path.Combine(dir, name + ".lua"), $"-- {name} {version}\n{body}");
    }

    public static string Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(dir);
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output.Trim();
    }

    public void Dispose()
    {
        foreach (var game in Library.Games)
            Sync.DeleteLocalData(game);
        try { AddonFiles.ForceDeleteDirectory(Root); } catch (IOException) { }
    }
}
