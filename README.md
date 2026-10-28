<div align="center">

# iscLauncher

A Windows launcher for World of Warcraft that keeps your addons and settings in sync across PCs and game versions.

[![Release](https://img.shields.io/github/v/release/iiisc/iscLauncher?style=flat-square&color=C9A84C&label=Release)](https://github.com/iiisc/iscLauncher/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/iiisc/iscLauncher/total?style=flat-square&color=3D7A5C&label=Downloads)](https://github.com/iiisc/iscLauncher/releases/latest)
![Windows 10 1809+](https://img.shields.io/badge/Windows%2010%201809+-0078D6?style=flat-square&logo=windows&logoColor=white)

<img src="Assets/iscLauncherScreenshot.png" alt="iscLauncher" width="820">

</div>

## Features

- **Private servers:** sets the realmlist and realm before every launch and logs you in, with the password kept in Windows Credential Manager.
- **Official installs:** any version (Classic Era, beta, ...). With a saved password it logs in directly; without one it opens Battle.net, where you press Play.
- **Sync between PCs:** push addons and settings from one PC and pull them on another, through a private GitHub repo.
- **Addon updates** for addons hosted on GitHub, plus a list of everything installed.
- **Copy addons** and their settings between games.

Every change is previewed first, lets you pick what to apply, and backs up anything it replaces.

## Install

Download the `.zip` from the [latest release](https://github.com/iiisc/iscLauncher/releases/latest), extract it, and run `iscLauncher.exe`. If SmartScreen warns on first launch, choose **More info → Run anyway**.

## Getting started

1. Click **+ Add Game** and pick an install under **Found on this PC**, or browse to the game's `.exe`.
2. For a private server, fill in the realmlist, account, realm and password.
3. Click **Save Changes**, then **Launch**.

To sync, open the game's **Sync** tab and click **Set Up Sync** on each PC. Then **Push** on one and **Pull** on the other. After that the app offers to pull before you play and to push when the game closes.

## Privacy

iscLauncher only talks to GitHub, and only to the repos you give it. It can't see your other projects or change any repo's settings. Game passwords never leave your PC.

<details>
<summary><b>Uninstall</b></summary>
<br>

1. Delete the app's folder, `%APPDATA%\IscLauncher`, `%LOCALAPPDATA%\IscLauncher` and the Start menu shortcut if you added one.
2. Remove the `iscLauncher_` entries under **Credential Manager → Windows Credentials**.
3. On GitHub, uninstall iscLauncher under **Settings → Applications → Installed GitHub Apps**.

</details>

<details>
<summary><b>Building from source</b></summary>
<br>

Requires the .NET 10 SDK. Run `dotnet build`.

`dotnet publish -c Release` builds the native release and also needs Visual Studio's **Desktop development with C++** workload. If it fails with "'vswhere.exe' is not recognized", add `C:\Program Files (x86)\Microsoft Visual Studio\Installer` to your PATH.

</details>

## License

[MIT](LICENSE)
