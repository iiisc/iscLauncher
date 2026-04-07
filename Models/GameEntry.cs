using System;
using System.IO;
using System.Text.Json.Serialization;

namespace iscLauncher.Models;

public enum PasswordInputMethod
{
    /// <summary>
    /// Use keyboard simulation to type the password (best for DirectX/OpenGL games like WoW)
    /// </summary>
    SendKeys = 0,

    /// <summary>
    /// Just copy the password to clipboard (manual paste)
    /// </summary>
    Clipboard = 1
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

    /// <summary>
    /// Seconds to wait after the process starts before attempting to type the password.
    /// Needed for DirectX/OpenGL games whose login screen takes time to render on cold starts.
    /// </summary>
    [JsonPropertyName("startupDelay")]
    public int StartupDelaySeconds { get; set; } = 0;

    /// <summary>
    /// HTTPS URL of the GitHub repo used for addon/WTF sync.
    /// </summary>
    [JsonPropertyName("syncRepoUrl")]
    public string? SyncRepoUrl { get; set; }

    /// <summary>
    /// Branch to clone/pull (defaults to "main" in service logic when null).
    /// </summary>
    [JsonPropertyName("syncBranch")]
    public string? SyncBranch { get; set; }

    /// <summary>
    /// UTC timestamp of the last successful pull or push.
    /// </summary>
    [JsonPropertyName("lastSynced")]
    public DateTime? LastSynced { get; set; }

    [JsonIgnore]
    public string CredentialTarget => $"iscLauncher_{Id}";

    [JsonIgnore]
    public bool HasRealmName => !string.IsNullOrWhiteSpace(RealmName);

    [JsonIgnore]
    public bool HasAccountName => !string.IsNullOrWhiteSpace(AccountName);

    [JsonIgnore]
    public bool HasServerInfo => HasRealmName || HasAccountName;

    [JsonIgnore]
    public bool HasSyncRepo => !string.IsNullOrWhiteSpace(SyncRepoUrl);

    /// <summary>
    /// Short display label for the executable: "ParentFolder\FileName" or just "FileName".
    /// </summary>
    [JsonIgnore]
    public string ExecutableDisplayPath
    {
        get
        {
            if (string.IsNullOrEmpty(ExecutablePath)) return string.Empty;
            var dir = Path.GetDirectoryName(ExecutablePath);
            var dirName = string.IsNullOrEmpty(dir) ? null : Path.GetFileName(dir);
            var fileName = Path.GetFileName(ExecutablePath);
            return dirName != null ? $"{dirName}\\{fileName}" : fileName;
        }
    }
}
