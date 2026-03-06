using System;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml;

namespace iscLauncher.Models;

public enum PasswordInputMethod
{
    /// <summary>
    /// Use keyboard simulation to type the password (best for DirectX/OpenGL games like WoW)
    /// </summary>
    SendKeys = 0,

    /// <summary>
    /// Use UI Automation to find and fill the password field (for standard Windows apps)
    /// </summary>
    UIAutomation = 1,

    /// <summary>
    /// Just copy the password to clipboard (manual paste)
    /// </summary>
    Clipboard = 2
}

public class GameEntry
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("executablePath")]
    public string ExecutablePath { get; set; } = string.Empty;

    [JsonPropertyName("windowTitle")]
    public string? WindowTitle { get; set; }

    [JsonPropertyName("inputMethod")]
    public PasswordInputMethod InputMethod { get; set; } = PasswordInputMethod.SendKeys;

    /// <summary>
    /// The realmlist server address (e.g., "logon.warmane.com").
    /// When set, the launcher will update Data\enUS\realmlist.wtf before launching.
    /// </summary>
    [JsonPropertyName("realmlistAddress")]
    public string? RealmlistAddress { get; set; }

    /// <summary>
    /// The account name to use for login.
    /// When set, the launcher will update WTF\config.txt before launch.
    /// </summary>
    [JsonPropertyName("accountName")]
    public string? AccountName { get; set; }

    /// <summary>
    /// The realm/server name (e.g., "ChromieCraft").
    /// When set, the launcher will update WTF\config.txt before launch.
    /// </summary>
    [JsonPropertyName("realmName")]
    public string? RealmName { get; set; }

    [JsonIgnore]
    public string CredentialTarget => $"iscLauncher_{Id}";

    /// <summary>
    /// Returns Visible if RealmName is set, otherwise Collapsed.
    /// </summary>
    [JsonIgnore]
    public Visibility HasRealmName => string.IsNullOrWhiteSpace(RealmName) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// Returns Visible if AccountName is set, otherwise Collapsed.
    /// </summary>
    [JsonIgnore]
    public Visibility HasAccountName => string.IsNullOrWhiteSpace(AccountName) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// Returns true if any server info (realm name or account name) is configured.
    /// </summary>
    [JsonIgnore]
    public Visibility HasServerInfo => (HasRealmName == Visibility.Visible || HasAccountName == Visibility.Visible) ? Visibility.Visible : Visibility.Collapsed;
}
