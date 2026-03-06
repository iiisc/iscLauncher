# ISC Game Launcher - Application Plan

## Overview
A Windows desktop application that manages game shortcuts and automatically inputs stored passwords when launching games.

---

## ✅ Implemented Features

### 1. Game Management
- ✅ **Add Game**: User selects an executable file and provides a password
- ✅ **Remove Game**: User can delete a game from the launcher
- ✅ **Edit Game**: User can update the executable path or password
- ✅ **List Games**: Display all registered games with their names and icons

### 2. Credential Storage
- ✅ Passwords stored securely in **Windows Credential Manager**
- ✅ Credential naming convention: `iscLauncher_{guid}`
- ✅ GUID is generated when a game is added and links the game entry to its credential

### 3. Game Launching
- ✅ Start the game executable
- ✅ Retrieve password from Windows Credential Manager
- ✅ Automatically input password into the game's password field
- ✅ Press Enter to submit the password

### 4. Password Input Methods
- ✅ **SendKeys**: Simulates keyboard typing (best for DirectX/OpenGL games like WoW)
- ✅ **UI Automation**: Uses Windows UI Automation API (for standard Windows apps)
- ✅ **Clipboard**: Copies password to clipboard for manual paste
- ✅ User can select the input method per game

### 5. Auto-Detection
- ✅ **App Type Detection**: Automatically detects if executable is a game or Windows app
- ✅ Checks for DirectX/OpenGL DLLs, Steam libraries, Unity/Unreal engines
- ✅ Suggests appropriate password input method based on detection

### 6. User Interface
- ✅ **WinUI 3** with Mica backdrop
- ✅ **Custom title bar** with app branding
- ✅ **Card-style game list** with rounded corners
- ✅ **Game icons** extracted from executable files
- ✅ **Status bar** with copy functionality
- ✅ **Empty state** when no games added
- ✅ Modern Windows 11 visual style

---

## Workflow

### Adding a New Game
1. User clicks "Add Game"
2. File dialog opens → User selects the game executable (.exe)
3. **Auto-detects** app type and suggests input method
4. Prompt for a **display name** for the game (auto-filled from filename)
5. Prompt for the **password** for this game
6. **Optional**: Set window title pattern for automation
7. Generate a new GUID for this game
8. Store password in Windows Credential Manager as `iscLauncher_{guid}`
9. Save game entry to configuration file

### Launching a Game
1. User clicks "Launch" on a game (or double-clicks)
2. Status shows "Launching..." immediately
3. Fetch password from Windows Credential Manager
4. Start the game process
5. Wait for game window (polls every 100ms, 3s timeout)
6. Based on input method:
   - **SendKeys**: Focus window, type password, press Enter
   - **UI Automation**: Find password field, set value, press Enter
   - **Clipboard**: Copy password to clipboard
7. Show success/error status

---

## Data Storage

### Configuration File
- **Format**: JSON via `System.Text.Json`
- **Location**: `%APPDATA%\IscLauncher\games.json`

#### Schema
```json
{
  "games": [
    {
      "id": "550e8400-e29b-41d4-a716-446655440000",
      "name": "My Game",
      "executablePath": "C:\\Games\\MyGame\\game.exe",
      "windowTitle": "My Game Login",
      "inputMethod": 0
    }
  ]
}
```

**InputMethod values**: 0 = SendKeys, 1 = UIAutomation, 2 = Clipboard

### Windows Credential Manager
- Type: Generic Credential
- Target Name: `iscLauncher_{id}`
- Password: The actual game password (encrypted by Windows)

---

## Tech Stack (Implemented)

| Component | Technology |
|-----------|------------|
| Framework | .NET 8 |
| UI | WinUI 3 (Windows App SDK) |
| Backdrop | Mica |
| JSON Handling | System.Text.Json |
| Credential Manager | P/Invoke to advapi32.dll |
| UI Automation | FlaUI.UIA3 |
| Icon Extraction | Shell32 + System.Drawing.Common |
| File Dialogs | Windows.Storage.Pickers |

---

## Project Structure

```
iscLauncher/
├── iscLauncher.csproj
├── App.xaml / App.xaml.cs
├── MainWindow.xaml / .cs           # Main game list UI with custom title bar
├── Dialogs/
│   └── GameDialog.xaml / .cs       # ContentDialog for adding/editing games
├── Models/
│   ├── GameEntry.cs                # Game data model with InputMethod enum
│   └── GameLibrary.cs              # Collection wrapper for JSON serialization
├── Services/
│   ├── GameRepository.cs           # Load/save games from JSON
│   ├── CredentialService.cs        # Windows Credential Manager interaction
│   ├── GameLauncherService.cs      # Start game and orchestrate password entry
│   ├── PasswordAutomationService.cs # SendKeys and UI Automation
│   ├── AppTypeDetector.cs          # Detect game vs Windows app
│   └── IconExtractor.cs            # Extract icons from executables
└── Assets/                         # App icons and images
```

---

## Automation Settings

| Setting | Value |
|---------|-------|
| Window poll interval | 100ms |
| Window timeout | 3 seconds |
| Delay before typing | 50ms |
| Delay before Enter | 50ms |

---

## Security Notes

- ✅ Passwords are stored in Windows Credential Manager (encrypted by Windows)
- ✅ The JSON config file does **not** contain passwords, only GUIDs
- ✅ Clipboard is cleared after 30 seconds when using clipboard fallback
- ⚠️ UI automation could be detected by some games with anti-cheat

---

## Future Enhancements (Out of Scope for MVP)

- [ ] Import/export game list (without passwords)
- [ ] Game categories/folders
- [x] ~~Game icons/thumbnails~~ ✅ Implemented
- [ ] Hotkey support for quick launch
- [ ] Minimize to system tray
- [ ] Auto-start with Windows
- [ ] Multiple profiles per game
- [ ] Configurable delays per game
- [ ] Username field automation

---

## Completed Steps

1. ~~Set up .NET 8 WinUI 3 project~~ ✅
2. ~~Implement `GameEntry` model and JSON repository~~ ✅
3. ~~Implement Windows Credential Manager service~~ ✅
4. ~~Build main window with game list~~ ✅
5. ~~Build add/edit game dialog~~ ✅
6. ~~Implement game launching with password automation~~ ✅
7. ~~Add SendKeys support for DirectX/OpenGL games~~ ✅
8. ~~Add UI Automation support for Windows apps~~ ✅
9. ~~Add auto-detection of app type~~ ✅
10. ~~Add game icon extraction~~ ✅
11. ~~Polish UI with modern WinUI 3 design~~ ✅
12. ~~Custom title bar~~ ✅
13. Test with real games ⏳
