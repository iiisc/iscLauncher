# Addon & WTF Settings Sync — Feature Plan

## Overview

Add the ability for each `GameEntry` to optionally link a **GitHub repository** that serves as the single source of truth for addons and WTF character settings.

- **Pull (Sync Down):** Clone/pull the repo, copy addons into `Interface/AddOns/`, and fan out WTF settings to every character on disk.
- **Push (Upload):** Export the current local addons and a chosen character's WTF settings back into the repo structure and push to GitHub — so the repo always reflects your latest setup.

---

## Folder Structure (Relative to Game Executable)

```
<game.exe directory>/
├── Interface/
│   └── AddOns/                    ← all addons live here
└── WTF/
    ├── Config.wtf
    └── Account/
        └── <ACCOUNT>/                    (e.g. ISC3S)
            ├── SavedVariables/           account-level addon data folder
            │   ├── GearScoreLite.lua
            │   └── ...
            ├── SavedVariables.lua        account-level saved variables (root file)
            ├── SavedVariables.lua.bak
            ├── bindings-cache.wtf        keybindings
            ├── bindings-cache.old
            ├── config-cache.wtf          account config cache
            ├── config-cache.old
            ├── macros-cache.txt          macros
            ├── macros-cache.old
            ├── cache.md5
            └── <SERVER>/                 (e.g. Onyxia)
                └── <CHARACTER>/          (e.g. Isc)
                    ├── SavedVariables/   character-level addon data folder
                    ├── SavedVariables.lua
                    ├── SavedVariables.lua.bak
                    ├── layout-local.txt  action bar / UI layout
                    ├── chat-cache.txt
                    ├── chat-cache.old
                    ├── config-cache.wtf
                    ├── config-cache.old
                    ├── macros-cache.txt
                    ├── macros-cache.old
                    └── cache.md5
```

---

## GitHub Repo Expected Layout

The linked repository must follow this structure:

```
<repo-root>/
├── AddOns/                        → synced to Interface/AddOns/
│   ├── Bartender4/
│   ├── Details/
│   └── ...
└── WTF/
    ├── AccountTemplate/           → copied into every Account/<X>/ (account-level files)
    │   ├── SavedVariables/        account-level addon data
    │   │   ├── GearScoreLite.lua
    │   │   └── ...
    │   ├── SavedVariables.lua
    │   ├── bindings-cache.wtf     keybindings
    │   ├── config-cache.wtf       account config cache
    │   ├── macros-cache.txt       macros
    │   └── cache.md5
    └── CharacterTemplate/         → fanned out to every Account/<X>/<Server>/<Char>/
        ├── SavedVariables/        character-level addon data
        │   └── ...
        ├── SavedVariables.lua
        ├── layout-local.txt
        ├── chat-cache.txt
        ├── config-cache.wtf
        ├── macros-cache.txt
        └── cache.md5
```

**Key concepts:**
- `WTF/AccountTemplate/` is the "golden" account folder. During sync its contents (SavedVariables/, bindings, macros, config-cache, etc.) are copied into every `Account/<X>/` on disk — so all accounts share the same keybindings, macros, and account-level addon data.
- `WTF/CharacterTemplate/` is the "golden" character folder. Its contents are copied into *every* character directory — so all characters share the same UI layout, chat settings, and character-level addon data.
- `.old` and `.bak` files are **excluded** from sync (they are auto-generated backups the game recreates).

---

## Changes

### 1. Model — `Models/GameEntry.cs`

Add three new optional JSON-serialized properties:

| Property | Type | Default | Purpose |
|---|---|---|---|
| `SyncRepoUrl` | `string?` | `null` | HTTPS URL of the GitHub repo (e.g. `https://github.com/iiisc/wow-sync.git`) |
| `SyncBranch` | `string?` | `null` | Branch to pull (defaults to `"main"` in service logic when null) |
| `LastSynced` | `DateTime?` | `null` | UTC timestamp of the last successful pull or push. Displayed in the UI as a relative time (e.g. "2 min ago"). |

Add one computed property:

| Property | Type | Purpose |
|---|---|---|
| `HasSyncRepo` | `bool` (`[JsonIgnore]`) | `!string.IsNullOrWhiteSpace(SyncRepoUrl)` — controls UI visibility and the sync badge on the game list card |

These serialize/deserialize with the existing `games.json` via `System.Text.Json`. No schema migration needed; missing properties default to `null`.

---

### 2. New Service — `Services/GitService.cs`

Thin wrapper around the `git` CLI. No new NuGet packages — uses `System.Diagnostics.Process` (same pattern as `GameLauncherService`).

| Method | Signature | Purpose |
|---|---|---|
| `IsGitInstalledAsync` | `Task<bool>` | Runs `git --version`; returns false if not found |
| `CloneAsync` | `Task<GitResult> CloneAsync(string repoUrl, string branch, string targetDir, CancellationToken ct)` | `git clone --branch {branch} {url} {dir}` (full clone — needed for push) |
| `PullAsync` | `Task<GitResult> PullAsync(string repoDir, CancellationToken ct)` | `git -C {dir} pull` |
| `AddAllAsync` | `Task<GitResult> AddAllAsync(string repoDir, CancellationToken ct)` | `git -C {dir} add -A` |
| `CommitAsync` | `Task<GitResult> CommitAsync(string repoDir, string message, CancellationToken ct)` | `git -C {dir} -c user.name="iscLauncher" -c user.email="noreply" commit -m "{message}"` — fallback identity so commit never fails if the user has no global git config |
| `PushAsync` | `Task<GitResult> PushAsync(string repoDir, CancellationToken ct)` | `git -C {dir} push` |
| `GetRemoteUrlAsync` | `Task<string?> GetRemoteUrlAsync(string repoDir, CancellationToken ct)` | `git -C {dir} remote get-url origin` — used to detect when the user changed `SyncRepoUrl` |
| `IsStatusCleanAsync` | `Task<bool> IsStatusCleanAsync(string repoDir, CancellationToken ct)` | `git -C {dir} status --porcelain` — returns `true` if output is empty (nothing to commit) |
| `GetLocalCachePath` | `string GetLocalCachePath(GameEntry game)` | Returns `%AppData%/IscLauncher/SyncCache/{game.Id}` |

Return type: `record GitResult(bool Success, string Output);`

---

### 3. New Service — `Services/AddonSyncService.cs`

Orchestrates the full sync workflow.

**Constructor dependencies:** `GitService`

**Primary methods:**

```csharp
// Pull from repo → local game directory
Task<SyncResult> SyncAsync(GameEntry game, IProgress<string>? progress = null, CancellationToken ct = default);

// Push local game directory → repo
Task<SyncResult> UploadAsync(GameEntry game, string sourceCharacterPath, IProgress<string>? progress = null, CancellationToken ct = default);
```

`sourceCharacterPath` is the full path to one specific character folder (e.g. `WTF/Account/ISC3S/Warmane/Isc`) whose settings become the new `CharacterTemplate`.

**Return type:**

```csharp
record SyncResult(bool Success, string Message, int AddOnsCopied, int CharactersSynced);
```

#### Pull flow — `SyncAsync` (executed in order):

| # | Step | Detail |
|---|---|---|
| 1 | **Validate** | Check `git` is installed, `SyncRepoUrl` is set, game executable exists |
| 2 | **Clone or Pull** | If cache folder doesn't exist → `CloneAsync`. If it exists, call `GetRemoteUrlAsync` and compare to `SyncRepoUrl` — on mismatch, delete the cache folder and re-clone (handles the user changing repos). Otherwise → `PullAsync`. |
| 3 | **Sync AddOns** | Mirror `<cache>/AddOns/*` → `<gameDir>/Interface/AddOns/`. Copy new/updated addon folders. The repo should never contain `Blizzard_*` folders (they are excluded during push), but if present they are copied like any other addon. |
| 4 | **Sync Account-Level Files** | If `<cache>/WTF/AccountTemplate/` exists, copy its entire contents (SavedVariables/, bindings-cache.wtf, macros-cache.txt, config-cache.wtf, SavedVariables.lua, cache.md5) into every `<gameDir>/WTF/Account/<Account>/` found on disk. Skip `.old` and `.bak` files. |
| 5 | **Fan-Out Character Settings** | Enumerate all `<gameDir>/WTF/Account/<Account>/<Server>/<Character>/` folders. For each, overwrite with contents of `<cache>/WTF/CharacterTemplate/*`. Skip `.old` and `.bak` files. |
| 6 | **Update timestamp** | Set `game.LastSynced = DateTime.UtcNow` and persist via `GameRepository.UpdateGameAsync`. |
| 7 | **Return result** | Counts of addons synced and characters updated |

#### Push flow — `UploadAsync` (executed in order):

| # | Step | Detail |
|---|---|---|
| 1 | **Validate** | Check `git` is installed, `SyncRepoUrl` is set, game executable exists, `sourceCharacterPath` exists |
| 2 | **Clone or Pull** | Ensure cache repo is up to date (same remote-URL-mismatch check as pull step 2) |
| 3 | **Export AddOns** | Clear `<cache>/AddOns/` and copy `<gameDir>/Interface/AddOns/*` into it, **skipping `Blizzard_*` folders** (built-in game addons containing only `.pub` stubs — no value in syncing them). |
| 4 | **Export Account Template** | Find the account folder that contains `sourceCharacterPath`. Clear `<cache>/WTF/AccountTemplate/` and copy account-level contents into it: `SavedVariables/`, `SavedVariables.lua`, `bindings-cache.wtf`, `macros-cache.txt`, `config-cache.wtf`, `cache.md5`. Exclude `.old` and `.bak` files. |
| 5 | **Export Character Template** | Clear `<cache>/WTF/CharacterTemplate/` and copy the contents of `sourceCharacterPath` into it. Exclude `.old` and `.bak` files. |
| 6 | **Commit & Push** | `git add -A` → `IsStatusCleanAsync` — if clean, skip commit/push and return *"Already up to date — nothing to push."* Otherwise → `CommitAsync` → `PushAsync`. |
| 7 | **Update timestamp** | Set `game.LastSynced = DateTime.UtcNow` and persist via `GameRepository.UpdateGameAsync`. |
| 8 | **Return result** | Counts of addons and files exported |

**Progress reporting:** Each step calls `progress?.Report("...")` so the UI can show a live status.

---

### 4. UI — `MainWindow.xaml` (Detail Panel)

Add a new **"🔄 ADDON SYNC"** section in the detail panel's `ScrollViewer`, positioned between the CONNECTION and AUTOMATION sections. Follows the existing section pattern:

```xml
<!-- Addon Sync Section (visible only when SyncRepoUrl is configured) -->
<StackPanel x:Name="AddonSyncSection" Spacing="8" Visibility="Collapsed">
    <TextBlock Text="🔄 ADDON SYNC"
               FontFamily="{StaticResource DisplayFont}"
               FontSize="9"
               CharacterSpacing="150"
               Foreground="{StaticResource GoldDarkBrush}"/>

    <!-- Edit-mode fields for repo URL and branch -->
    <StackPanel Spacing="4">
        <TextBlock Text="GitHub Repo URL" .../>
        <TextBox x:Name="EditSyncRepoUrlTextBox" .../>
    </StackPanel>

    <StackPanel Spacing="4">
        <TextBlock Text="Branch" .../>
        <TextBox x:Name="EditSyncBranchTextBox" PlaceholderText="main" .../>
    </StackPanel>

    <!-- Last synced timestamp -->
    <TextBlock x:Name="LastSyncedText"
               FontFamily="{StaticResource BodyFont}"
               FontSize="10"
               Foreground="{StaticResource TextDimBrush}"
               FontStyle="Italic"
               Visibility="Collapsed"/>

    <!-- Sync buttons + progress + cancel (view-mode only) -->
    <StackPanel x:Name="SyncButtonsPanel" Orientation="Horizontal" Spacing="10">
        <Button x:Name="SyncAddonsButton"
                Style="{StaticResource SecondaryButtonStyle}"
                Click="OnSyncAddonsClick" ...>
            <StackPanel Orientation="Horizontal" Spacing="8">
                <FontIcon Glyph="&#xE896;" FontSize="14"/>  <!-- Download -->
                <TextBlock Text="Pull" .../>
            </StackPanel>
        </Button>

        <Button x:Name="UploadAddonsButton"
                Style="{StaticResource SecondaryButtonStyle}"
                Click="OnUploadAddonsClick" ...>
            <StackPanel Orientation="Horizontal" Spacing="8">
                <FontIcon Glyph="&#xE898;" FontSize="14"/>  <!-- Upload -->
                <TextBlock Text="Push" .../>
            </StackPanel>
        </Button>

        <ProgressRing x:Name="SyncProgressRing" IsActive="False" .../>

        <Button x:Name="CancelSyncButton"
                Style="{StaticResource SecondaryButtonStyle}"
                Click="OnCancelSyncClick"
                Visibility="Collapsed" ...>
            <StackPanel Orientation="Horizontal" Spacing="6">
                <FontIcon Glyph="&#xE711;" FontSize="12"/>  <!-- Cancel -->
                <TextBlock Text="Cancel" .../>
            </StackPanel>
        </Button>
    </StackPanel>

    <!-- Character picker for upload (shown when Push is clicked) -->
    <StackPanel x:Name="CharacterPickerPanel" Spacing="4" Visibility="Collapsed">
        <TextBlock Text="Select source character"
                   FontFamily="{StaticResource BodyFont}"
                   FontSize="10"
                   Foreground="{StaticResource TextMutedBrush}"/>
        <ComboBox x:Name="CharacterPickerComboBox"
                  Style="{StaticResource ThemedComboBoxStyle}"
                  HorizontalAlignment="Stretch" .../>
        <TextBlock Text="This character's settings will become the template for all characters."
                   FontFamily="{StaticResource BodyFont}"
                   FontSize="10"
                   Foreground="{StaticResource TextDimBrush}"
                   FontStyle="Italic"
                   TextWrapping="Wrap"/>
        <Button x:Name="ConfirmUploadButton"
                Style="{StaticResource SecondaryButtonStyle}"
                Click="OnConfirmUploadClick" ...>
            <StackPanel Orientation="Horizontal" Spacing="8">
                <FontIcon Glyph="&#xE898;" FontSize="14"/>
                <TextBlock Text="Upload Now" .../>
            </StackPanel>
        </Button>
    </StackPanel>
</StackPanel>
```

**Visibility logic:**
- The entire section is visible in **edit mode** (so the user can configure the repo URL).
- In **view mode**, the section is visible only when `HasSyncRepo` is true — showing Pull/Push buttons.
- `SyncAddonsButton` and `UploadAddonsButton` are disabled during edit mode; the `EditSyncRepoUrlTextBox` / `EditSyncBranchTextBox` are disabled outside edit mode. (Follows the same enable/disable pattern already used for all other fields.)
- `CharacterPickerPanel` is hidden by default. It becomes visible when Push is clicked, populated with discovered character paths. After confirming, it hides again.
- `CancelSyncButton` is hidden by default. Shown alongside `SyncProgressRing` during active sync operations.
- `LastSyncedText` is visible only when `game.LastSynced` has a value.

**Game list card badge:**

In the `ListView.ItemTemplate` (left panel), add a small sync icon next to the existing server/account badges:

```xml
<StackPanel Orientation="Horizontal" Spacing="4"
            Visibility="{x:Bind converters:BoolToVisibilityConverter.ToVisibility(HasSyncRepo)}">
    <FontIcon Glyph="&#xE895;" FontSize="10" Foreground="{StaticResource GoldBrush}" />
    <TextBlock Text="Sync"
               FontFamily="{StaticResource BodyFont}"
               FontSize="10"
               Foreground="{StaticResource GoldBrush}" />
</StackPanel>
```

This reuses the existing `HasServerInfo` badge pattern and the `BoolToVisibilityConverter.ToVisibility` static helper already used in the template.

---

### 5. UI — `MainWindow.xaml.cs`

| Change | Detail |
|---|---|
| **New fields** | `private readonly AddonSyncService _addonSyncService = new(new GitService());` and `private CancellationTokenSource? _syncCts;` for cancellation support. |
| **`ShowGameDetails`** | Populate `EditSyncRepoUrlTextBox` and `EditSyncBranchTextBox` from `game.SyncRepoUrl` / `game.SyncBranch`. Set `AddonSyncSection.Visibility` based on `HasSyncRepo` or edit mode. Display `LastSyncedText` — if `game.LastSynced` is set, show relative time (e.g. *"Last synced: 5 min ago"*), otherwise hide. |
| **`SetEditMode`** | Add `EditSyncRepoUrlTextBox.IsEnabled = isEditing;`, `EditSyncBranchTextBox.IsEnabled = isEditing;`, disable `SyncAddonsButton` / `UploadAddonsButton` while editing. Show `AddonSyncSection` when editing. |
| **`OnSaveEditClick`** | Read sync fields and assign to `_currentEditingGame.SyncRepoUrl` / `.SyncBranch`. |
| **New handler: `OnSyncAddonsClick`** | Creates `_syncCts`, disables Pull/Push buttons, shows `SyncProgressRing` + `CancelSyncButton`, calls `_addonSyncService.SyncAsync(game, progress, _syncCts.Token)`, on completion hides cancel button, refreshes `LastSyncedText`, shows result via `ShowStatus`. |
| **New handler: `OnUploadAddonsClick`** | Enumerates all character folders under `<gameDir>/WTF/Account/`, populates `CharacterPickerComboBox` with display names (`Account/Server/Character`), and shows `CharacterPickerPanel`. |
| **New handler: `OnConfirmUploadClick`** | Creates `_syncCts`, gets selected character path, disables buttons, shows `SyncProgressRing` + `CancelSyncButton`, calls `_addonSyncService.UploadAsync(game, selectedPath, progress, _syncCts.Token)`, on completion refreshes `LastSyncedText`, hides cancel button + `CharacterPickerPanel`, shows result via `ShowStatus`. |
| **New handler: `OnCancelSyncClick`** | Calls `_syncCts?.Cancel()`. The service methods observe the token and abort gracefully. UI shows *"Sync cancelled."* via `ShowStatus`. |

---

### 6. UI — `Dialogs/GameDialog.xaml` + `GameDialog.xaml.cs`

Add an **"ADDON SYNC"** section to the right column (below the ADVANCED section):

| Field | Control | Maps to |
|---|---|---|
| GitHub Repo URL | `TextBox` (`SyncRepoUrlTextBox`) | `GameEntry.SyncRepoUrl` |
| Branch | `TextBox` (`SyncBranchTextBox`, placeholder `"main"`) | `GameEntry.SyncBranch` |

In `OnPrimaryButtonClick`, map the values:
```csharp
GameEntry.SyncRepoUrl = string.IsNullOrWhiteSpace(SyncRepoUrlTextBox.Text) ? null : SyncRepoUrlTextBox.Text.Trim();
GameEntry.SyncBranch = string.IsNullOrWhiteSpace(SyncBranchTextBox.Text) ? null : SyncBranchTextBox.Text.Trim();
```

In the constructor, populate fields when editing an existing game.

---

## File Summary

| Action | File | What |
|---|---|---|
| **Modify** | `Models/GameEntry.cs` | Add `SyncRepoUrl`, `SyncBranch`, `HasSyncRepo` |
| **Create** | `Services/GitService.cs` | Git CLI wrapper |
| **Create** | `Services/AddonSyncService.cs` | Sync orchestration (clone/pull, copy addons, fan-out WTF) |
| **Modify** | `MainWindow.xaml` | Add Addon Sync section to detail panel |
| **Modify** | `MainWindow.xaml.cs` | Wire up sync button, edit fields, progress UI |
| **Modify** | `Dialogs/GameDialog.xaml` | Add repo URL + branch fields |
| **Modify** | `Dialogs/GameDialog.xaml.cs` | Map new fields to/from `GameEntry` |

**No new NuGet packages.** Git is invoked via `System.Diagnostics.Process`.

---

## Edge Cases

| Case | Handling |
|---|---|
| `git` not installed | `SyncAsync` returns failure: *"Git is required for addon sync. Install from git-scm.com."* |
| No `WTF/Account/` folders yet (fresh install) | Sync addons only, skip WTF fan-out, return warning in message |
| Repo URL invalid / network error | Surface git stderr in `SyncResult.Message` → shown via `ShowStatus` |
| Sync while game is running | Allow (WoW reads WTF on startup, not while running) but note in status message to restart |
| Empty `CharacterTemplate/` in repo | Skip fan-out step, addons still sync normally |
| Repo has no `AddOns/` folder | Skip addon sync step, WTF still syncs normally |
| `SyncRepoUrl` cleared by user | `HasSyncRepo` becomes false, section hides, no sync possible — no data deleted |
| Push with no character folders | `UploadAsync` returns failure: *"No character folders found under WTF/Account/."* |
| Push with nothing changed | After `git add -A`, `IsStatusCleanAsync` returns true → skip commit/push, return *"Already up to date — nothing to push."* Avoids empty commits. |
| Push with uncommitted local cache changes | Pull first (step 2 of push flow) handles merging before overwriting and pushing |
| Push auth failure | Surface git stderr: *"Push failed — check that you have write access to the repo."* Git credential manager handles auth prompts. |
| `Blizzard_*` addon folders | Excluded from push (built-in stubs with only `.pub` files). Not expected in the repo; if present during pull they are copied normally. |
| `SyncRepoUrl` changed to a different repo | `GetRemoteUrlAsync` detects the mismatch → cache folder is deleted and re-cloned from the new URL. |
| No global `git user.name` / `user.email` | `CommitAsync` passes `-c user.name="iscLauncher" -c user.email="noreply"` as fallback so the commit never fails. |
| User cancels during sync | `CancellationToken` is observed by all git process calls and file-copy loops. Partial state in the cache folder is fine — the next sync will pull/overwrite. |

---

## Future Enhancements (Out of Scope)

- **Selective sync** — pick which addons or characters to include/exclude
- **Auto-sync on launch** — run sync automatically inside `LaunchGameAsync` before starting the process
- **Private repo support** — store a GitHub PAT in Windows Credential Manager (reuse `CredentialService`)
- **Delete orphaned addons** — remove local addons not in the repo (opt-in toggle on `GameEntry`)
