# iscLauncher

> Unified launcher for World of Warcraft private servers.

[![Release](https://img.shields.io/github/v/release/iiisc/iscLauncher?style=flat-square&color=C9A84C&label=Release)](https://github.com/iiisc/iscLauncher/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/iiisc/iscLauncher/total?style=flat-square&color=3D7A5C&label=Downloads)](https://github.com/iiisc/iscLauncher/releases/latest)
![Windows 10 1809+](https://img.shields.io/badge/Windows%2010%201809+-0078D6?style=flat-square&logo=windows&logoColor=white)

![iscLauncher](Assets/iscLauncherScreenshot.png)

iscLauncher manages multiple WoW private server installations from a single interface. It handles realmlist configuration, account pre-fill, credential-secured auto-login, and addon synchronisation across machines — eliminating all manual file editing.

---

## Table of Contents

- [Features](#features)
- [Requirements](#requirements)
- [Installation](#installation)
- [Quick Start](#quick-start)
- [Addon Sync](#addon-sync)
- [Security Model](#security-model)
- [Troubleshooting](#troubleshooting)
- [Contributing](#contributing)
- [License](#license)

---

## Features

| Feature | Description |
|---------|-------------|
| **Multi-server library** | Add any number of WoW installations. Each entry stores its executable path, server address, account name, and realm independently. |
| **Automatic realmlist update** | Before launch, `realmlist.wtf` is rewritten across all locale directories found under `Data\`. Falls back to creating `Data\enUS\realmlist.wtf` when none exist. |
| **WTF config pre-fill** | Account name, realm name, and server address are written to `WTF\Config.wtf` before the game process starts. |
| **Credential-backed auto-login** | Passwords are stored in Windows Credential Manager. On launch, iscLauncher polls for the game window (up to 15 seconds), then delivers the password via Win32 `SendInput` or the clipboard. |
| **Smart input detection** | Scans the game directory for DirectX, OpenGL, Vulkan, Steam, and known engine DLLs to automatically suggest the correct input method. |
| **Addon sync** | Link a private GitHub repository to push and pull `Interface\AddOns` and `WTF` settings between machines, with one-click rollback to any previous commit. |

---

## Requirements

| | Minimum |
|---|---------|
| **OS** | Windows 10 version 1809 (build 17763) |
| **Architecture** | x86 · x64 · ARM64 |
| **Runtime** | None — release is self-contained |

---

## Installation

1. Go to the [Latest Release](https://github.com/iiisc/iscLauncher/releases/latest).
2. Download the `.zip` archive.
3. Extract to any folder and run **iscLauncher.exe**.

No installer or additional runtime is required.

> **SmartScreen warning:** The binary is self-signed. If Windows displays a "Windows protected your PC" prompt, click **More info → Run anyway** to proceed.

---

## Quick Start

### Add a game

1. Click **+ Add Game** and select the WoW `.exe`.
2. Enter the **server address** (realmlist), **account name**, and **realm name**.
3. Enter your password — it is stored in Windows Credential Manager, not written to disk.
4. Double-click the game card or click **Launch**.

### What happens on launch

1. `realmlist.wtf` is updated with the configured server address.
2. `WTF\Config.wtf` is updated with the account and realm name.
3. The game process is started.
4. iscLauncher waits for the login window, then delivers the password automatically.

---

## Addon Sync

Addon sync requires a **private** GitHub repository.

1. Create a private repo (e.g. `my-wow-addons`).
2. Open a game's settings and paste the repository URL into the **Addon Sync** tab.
3. **Push** — uploads your `Interface\AddOns` and `WTF` folder to the repo.
4. **Pull** — downloads and applies them on another machine.
5. **Rollback** — reverts to the previous commit if a sync causes issues.

Each machine is identified by a configurable **computer name** (shown at the bottom-left of the UI), keeping per-machine keybindings separate from shared addon data.

---

## Security Model

| Aspect | Detail |
|--------|--------|
| **Password storage** | Windows Credential Manager, keyed as `iscLauncher_<game-id>`. Never written to disk or sent over the network. |
| **Clipboard mode** | The password is cleared from the clipboard automatically after **30 seconds**, and immediately on app shutdown. |
| **Credential removal** | Deleting a game entry removes its credential from Credential Manager. |
| **Addon sync transport** | GitHub HTTPS only. iscLauncher does not store sync credentials; authentication uses your existing Git configuration. |

---

## Troubleshooting

| Symptom | Resolution |
|---------|-----------|
| Password typed before the login screen is ready | Increase **Startup Delay** in the game's Automation settings. |
| Special characters are entered incorrectly | Switch **Input Method** from *SendKeys* to *Clipboard*. |
| Game launches but no password is entered | The launcher waits up to 15 seconds for the window to appear. Increase startup delay, or verify the window title pattern is correct. |
| Auto-login fails when game is run as administrator | Run iscLauncher as administrator so `SendInput` can target an elevated window. |
| Addon sync fails | Verify the repository URL, confirm the repo is private, and ensure your Git credentials have push/pull access. |

---

## Contributing

Bug reports and feature requests are welcome via [GitHub Issues](https://github.com/iiisc/iscLauncher/issues).
Pull requests should target the `master` branch.

---

## License

This project is licensed under the [MIT License](LICENSE).
