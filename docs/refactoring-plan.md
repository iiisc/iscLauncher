# iscLauncher Refactoring Plan

## Current State Summary

| File | Lines | Role |
|---|---|---|
| `MainWindow.xaml` | ~420 | Single-page layout: title bar, game list, detail/edit panel |
| `MainWindow.xaml.cs` | ~1,198 | **God class** — owns all UI logic, state, dialogs, sync orchestration |
| `Dialogs/GameDialog.xaml(.cs)` | ~240 | Add/edit game dialog (well-isolated) |
| `Services/AddonSyncService.cs` | ~456 | Clone/pull/push/rollback + file copy logic |
| `Services/GitService.cs` | ~181 | Git CLI wrapper |
| `Services/GameLauncherService.cs` | ~170 | Launch process, password entry, clipboard |
| `Services/PasswordAutomationService.cs` | ~350+ | Win32 SendInput automation |
| `Services/CredentialService.cs` | ~96 | Windows Credential Manager P/Invoke |
| `Services/RealmlistService.cs` | ~178 | Realmlist.wtf and Config.wtf file patching |
| `Services/GameRepository.cs` | ~98 | JSON file persistence with semaphore lock |
| `Services/IconExtractor.cs` | ~97 | Shell icon extraction + cache |
| `Services/AppTypeDetector.cs` | ~50+ | PE/DLL heuristic detection |
| `Models/GameEntry.cs` | ~96 | Data model + computed properties |
| `Models/GameLibrary.cs` | ~10 | Wrapper for JSON serialization |
| `Converters/BoolToVisibilityConverter.cs` | ~20 | Bool → Visibility |
| `App.xaml` | ~163 | Design tokens, colors, brushes, control styles |

**Tech stack:** WinUI 3 (WinAppSDK 1.8), .NET 8, no MVVM framework, no DI container.

---

## Problem Areas

### P1 — `MainWindow.xaml.cs` is a god class (1,198 lines)

The code-behind directly manages:
- Window chrome setup (DPI scaling, custom title bar)
- Game list loading, selection visuals, empty state
- Detail panel population + view/edit mode toggling (15+ controls)
- Game CRUD (add, delete, save edits)
- Game launching + process lifecycle tracking
- Three complex sync dialogs built entirely in C# (~450 lines of procedural UI)
- Status bar notifications with auto-hide timers
- Icon loading from data templates
- Dialog theming (`ApplyDialogTheme`)
- Commit-message body parsing

**Impact:** Untestable, hard to modify safely, merge-conflict magnet.

### P2 — Procedural dialog construction (~450 lines)

`OnSyncAddonsClick`, `OnUploadAddonsClick`, and `OnRollbackClick` each build a `ContentDialog` with complex inner UI (ListView, ComboBox, ScrollViewer, TextBlock trees) entirely in C#. This:
- Cannot be previewed in the XAML designer
- Duplicates styling/resource lookups across all three methods
- Makes layout changes error-prone

### P3 — Duplicated dialog theming

Every `ContentDialog` creation site repeats the same 8-property block (`Background`, `Foreground`, `BorderBrush`, `CornerRadius`, `RequestedTheme`) then calls `ApplyDialogTheme`. The static helper itself is 47 lines of style/resource overrides.

### P4 — Manual view/edit mode toggling

`SetEditMode` procedurally toggles `.IsEnabled` / `.Visibility` on 15+ named controls. Adding a field requires changes in `SetEditMode`, `ShowGameDetails`, and `OnSaveEditClick` — three places for one concept.

### P5 — Visual tree walking for selection state

`UpdateSelectionVisuals` iterates all `ListView` items, calls `FindVisualChild<Border>`, and manually sets `BorderBrush`, `BorderThickness`, `Background`, `Shadow`, and `Translation`. This breaks if the item template changes.

### P6 — No dependency injection

Services are `new`-ed inline in fields. `AddonSyncService` creates its own `GameRepository`, and `MainWindow` creates another — two independent instances accessing the same file with separate semaphore locks (potential race if both write concurrently).

### P7 — `GameRepository` is `new`-ed in two places

Both `MainWindow` and `AddonSyncService` instantiate `private readonly GameRepository _gameRepository = new()`. The semaphore `_fileLock` is an instance field, so the two instances do **not** share it — concurrent writes from sync + UI could corrupt `games.json`.

### P8 — `App.xaml` is both a theme file and style sheet

Colors, brushes, fonts, and four full button styles all live in one flat `ResourceDictionary`. At 163 lines it's manageable today but will grow.

---

## Proposed Refactorings

### Phase 1 — Extract Sync Dialogs (high impact, low risk)

**Goal:** Remove ~450 lines from `MainWindow.xaml.cs` with zero behavioral change.

| Task | Detail |
|---|---|
| **1a.** Create `Dialogs/SyncPullDialog.xaml(.cs)` | Move the pull-confirmation dialog (addon list, character warning, confirmation) into a proper `ContentDialog` subclass with XAML layout. Expose a `GameEntry` property and a `ShowAsync` result. |
| **1b.** Create `Dialogs/SyncPushDialog.xaml(.cs)` | Move the push-confirmation dialog (character picker ComboBox, warning text) into its own dialog. Expose `SelectedCharacterPath` on confirmation. |
| **1c.** Create `Dialogs/RollbackDialog.xaml(.cs)` | Move the rollback commit picker (ListView of commits, addon detail, warning) into its own dialog. Expose `SelectedCommit` on confirmation. |
| **1d.** Create `Helpers/DialogHelper.cs` | Extract `ApplyDialogTheme` into a static helper, and add a factory method `CreateThemedDialog(XamlRoot, string title)` that pre-applies all shared properties. Each dialog and `OnDeleteClick` calls this instead of duplicating 8 property setters. |
| **1e.** Move `ParseAddonListFromBody` | Move to `AddonSyncService` (or a new `CommitMessageParser` helper) — it has no UI dependency. |

**Estimated reduction:** MainWindow.xaml.cs drops from ~1,198 to ~700 lines.

---

### Phase 2 — Centralize State with a ViewModel (medium impact, medium risk)

**Goal:** Make state testable and reduce manual UI sync code.

| Task | Detail |
|---|---|
| **2a.** Create `ViewModels/MainViewModel.cs` | Owns `ObservableCollection<GameEntry> Games`, `GameEntry? SelectedGame`, `bool IsEditing`, `bool IsRunning`, `string StatusMessage`, `bool StatusIsSuccess`. Implements `INotifyPropertyChanged`. |
| **2b.** Move game CRUD operations | `LoadGamesAsync`, `AddGameAsync`, `DeleteGameAsync`, `SaveEditAsync` move to the ViewModel. Services are injected via constructor. |
| **2c.** Move launch + process tracking | `LaunchGameAsync`, `_runningGames` set, and `UpdateLaunchButtonState` logic move to the ViewModel. Expose `bool IsGameRunning(Guid id)` and a `RelayCommand LaunchCommand`. |
| **2d.** Bind XAML to ViewModel | Replace `x:Name` + code-behind population with `{x:Bind ViewModel.SelectedGame.Name, Mode=OneWay}` etc. `SetEditMode` becomes a single `IsEditing` property that the XAML reads via converters or `VisualStateManager`. |
| **2e.** Thin out `MainWindow.xaml.cs` | Code-behind retains only: window chrome setup, `DispatcherQueue` marshaling, and file-picker calls (which require `Window` handle). |

**Estimated reduction:** MainWindow.xaml.cs drops to ~200-250 lines. ViewModel is ~400 lines but fully unit-testable.

> **Note:** This does not require a third-party MVVM framework. A simple `ObservableObject` base class (or `CommunityToolkit.Mvvm` if desired) is sufficient.

---

### Phase 3 — Fix Shared `GameRepository` Instance (high impact, low risk)

**Goal:** Eliminate the potential data corruption from two unsynchronized `GameRepository` instances.

| Task | Detail |
|---|---|
| **3a.** Make `GameRepository` a singleton or shared instance | Either pass the same instance to both `MainWindow` and `AddonSyncService`, or use a static `SemaphoreSlim` so all instances share the same lock. |
| **3b.** (If doing Phase 2) Inject via ViewModel | The ViewModel holds the single `GameRepository`; `AddonSyncService` receives it through its constructor. |

**Simplest fix (standalone):** Change `_fileLock` from instance to `static`:
```csharp
private static readonly SemaphoreSlim _fileLock = new(1, 1);
```

---

### Phase 4 — Replace Visual Tree Walking with VisualStateManager (medium impact, low risk)

**Goal:** Remove fragile `FindVisualChild` + manual style manipulation.

| Task | Detail |
|---|---|
| **4a.** Define visual states in the `ListView.ItemTemplate` | Add a `VisualStateGroup` with `Selected` / `Unselected` states on `GameCardBorder`, controlling `BorderBrush`, `BorderThickness`, `Background`, `Translation`. |
| **4b.** Use `ListViewItem` container style with visual states | Override `ListViewItem` control template or use `Loaded`/`SelectionChanged` to trigger visual state changes. |
| **4c.** Delete `UpdateSelectionVisuals` and `FindVisualChild` | These become unnecessary (~35 lines removed). |

---

### Phase 5 — Split `App.xaml` Resources (low impact, low risk)

**Goal:** Keep resource files focused and navigable as the app grows.

| Task | Detail |
|---|---|
| **5a.** Create `Styles/Colors.xaml` | Move all `<Color>` and `<SolidColorBrush>` definitions. |
| **5b.** Create `Styles/Controls.xaml` | Move button styles, TextBox/PasswordBox/ComboBox styles. |
| **5c.** Update `App.xaml` `MergedDictionaries` | Reference the new resource files. Keep `Fonts.xaml` (already exists but is empty). |

---

### Phase 6 — Lightweight DI Setup (low impact, medium risk)

**Goal:** Formalize service lifetimes and eliminate scattered `new` calls.

| Task | Detail |
|---|---|
| **6a.** Add `Microsoft.Extensions.DependencyInjection` | Register services in `App.xaml.cs` with appropriate lifetimes (`Singleton` for `GameRepository`, `CredentialService`; `Transient` for `AddonSyncService`). |
| **6b.** Resolve in `App.OnLaunched` | Build `IServiceProvider`, pass to `MainWindow` (or ViewModel). |

> **Optional:** This can be deferred. The critical fix (Phase 3) doesn't require a DI container.

---

## Execution Order

```
Phase 3  →  Phase 1  →  Phase 4  →  Phase 2  →  Phase 5  →  Phase 6
(fix bug)   (biggest)   (cleanup)   (architecture)  (cosmetic)  (optional)
```

Phase 3 is a one-line bug fix and should go first. Phase 1 is the highest-value refactoring. Phase 2 is the largest architectural change and can be done incrementally (start with `SelectedGame` + `IsEditing`, expand from there).

---

## Files Created / Modified

| Phase | New Files | Modified Files |
|---|---|---|
| 1 | `Dialogs/SyncPullDialog.xaml(.cs)`, `Dialogs/SyncPushDialog.xaml(.cs)`, `Dialogs/RollbackDialog.xaml(.cs)`, `Helpers/DialogHelper.cs` | `MainWindow.xaml.cs` |
| 2 | `ViewModels/MainViewModel.cs`, `ViewModels/ObservableObject.cs` (or add CommunityToolkit.Mvvm) | `MainWindow.xaml`, `MainWindow.xaml.cs` |
| 3 | — | `Services/GameRepository.cs` |
| 4 | — | `MainWindow.xaml`, `MainWindow.xaml.cs` |
| 5 | `Styles/Colors.xaml`, `Styles/Controls.xaml` | `App.xaml` |
| 6 | — | `App.xaml.cs`, `MainWindow.xaml.cs` (or ViewModel constructor) |

---

## Out of Scope

- **Unit tests** — valuable but a separate effort; Phase 2 unblocks it.
- **Navigation framework** — not needed for a single-window app.
- **Third-party MVVM framework** — CommunityToolkit.Mvvm is recommended but not required; a 30-line `ObservableObject` base class is sufficient.
- **Async command pattern** — nice-to-have, can be added with Phase 2.
