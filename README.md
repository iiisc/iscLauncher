# iscLauncher

A Windows launcher for World of Warcraft that keeps your addons and settings in sync across PCs and game versions.

[![Release](https://img.shields.io/github/v/release/iiisc/iscLauncher?style=flat-square&color=C9A84C&label=Release)](https://github.com/iiisc/iscLauncher/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/iiisc/iscLauncher/total?style=flat-square&color=3D7A5C&label=Downloads)](https://github.com/iiisc/iscLauncher/releases/latest)
![Windows 10 1809+](https://img.shields.io/badge/Windows%2010%201809+-0078D6?style=flat-square&logo=windows&logoColor=white)

![iscLauncher](Assets/iscLauncherScreenshot.png)

## What it does

- **Private servers:** sets the realmlist and realm before every launch, and logs you in with a password kept in Windows Credential Manager.
- **Official installs:** add any version (Classic Era, beta, ...) and open it in Battle.net, where you press Play.
- **Sync between PCs:** push your addons and settings from one PC, pull them on another, through a private GitHub repo.
- **Update addons** that are on GitHub, and see every addon you have installed.
- **Copy addons** between games, or give your other characters the same addon setup as one character.

Every change is previewed first, lets you pick what to apply, and backs up anything it replaces.

## Install

Download the `.zip` from the [latest release](https://github.com/iiisc/iscLauncher/releases/latest) and extract it where you keep programs. It unpacks into an `iscLauncher` folder; run `iscLauncher.exe` in it. It needs 64-bit Windows 10 1809 or later, and nothing else.

If SmartScreen warns on first launch, choose **More info → Run anyway**. **Options → Add to Start Menu** makes it easy to find again. When a new version is out, a dot appears on the ⚙ Options button, where you can install it.

## Getting started

1. Click **+ Add Game**. Official versions on this PC are listed under **Found on this PC**; otherwise pick the game's `.exe`.
2. For a private server, fill in the realmlist, account and realm, and set your password under **Automation**.
3. Click **Save Changes**, then **Launch**.

If the password gets typed before the login screen is ready, raise **Startup Delay** under **Automation**.

## Syncing between PCs

Each game syncs with its own private repo on GitHub. Don't have a GitHub account? You can make a free one while you set up.

1. Open the game's **Sync** tab and click **Set Up Sync**. The app walks you through signing in, making the repo and giving iscLauncher access to it.
2. Click **Push** to upload this PC's addons and settings.
3. On your other PC, add the same game, click **Set Up Sync**, pick the repo and click **Pull**.

Sync uses Git. If the PC doesn't have it, the first push or pull asks to download MinGit (about 40 MB) from the Git for Windows project on GitHub. It's kept in `%LOCALAPPDATA%\IscLauncher\Git` for iscLauncher alone; nothing is installed in Windows.

After that you rarely need the buttons. The app offers to pull when you launch a game another PC has pushed to, and to push when the game closes. **Rollback** puts every PC back to an earlier push.

## Updating addons

The **Addons** tab lists every addon and checks the ones on GitHub for updates. Tick the ones you want and click **Update Selected**, or **Update All**. Games with updates waiting show **Addon updates** in the list.

Addons that name their GitHub project in their `.toc` are connected by themselves. For the others, click **Connect** and pick a match from GitHub, or paste a link.

## Your data

Everything lives in `%APPDATA%\IscLauncher` (**Options → Open Folder**): your library, backups (the last 3 per game, restored with **Sync → Restore a Backup**) and a log. Passwords and your GitHub sign-in are in Windows Credential Manager under `iscLauncher_...`.

iscLauncher only talks to GitHub, and only to the repos you give it: it can't see your other projects, or change any repo's settings. Change its repos under **Options → Choose which repos it can use**. Game passwords never leave your PC.

## Troubleshooting

| Problem | Fix |
|---------|-----|
| Password typed too early, or not at all | Raise **Startup Delay** under **Automation** |
| Login fails for a game that runs as administrator | Run iscLauncher as administrator too |
| Push or pull fails | Check that you're signed in under **Options**, and that the repo is one you've given iscLauncher |
| "GitHub turned down this PC's sign-in" | Sign in again under **Options** |
| Anything else | **Options → Report a Problem** opens a bug report with your version filled in |

## Uninstall

Delete the app's folder, `%APPDATA%\IscLauncher`, `%LOCALAPPDATA%\IscLauncher` (Git, if the app downloaded it) and the Start menu shortcut if you added one, remove the `iscLauncher_` entries from **Credential Manager → Windows Credentials**, and uninstall iscLauncher on GitHub under **Settings → Applications → Installed GitHub Apps**.

## Building from source

Requires the .NET 10 SDK.

```
dotnet build
```

To release, run the **Release** workflow from the Actions tab. It runs the tests, then makes a draft release of the next patch version, or the version you type in, such as `1.2.0`. Rewrite its notes for users (the app shows them before updating) and publish it.

## License

[MIT](LICENSE)
