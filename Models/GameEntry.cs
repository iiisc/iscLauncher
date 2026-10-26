using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text.Json.Serialization;

namespace iscLauncher.Models;

public class GameEntry : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Refreshes every binding to this entry. Call after editing its fields in place.</summary>
    public void NotifyChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    [JsonPropertyName("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("executablePath")]
    public string ExecutablePath { get; set; } = string.Empty;

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
    /// UTC timestamp of the last successful pull or push.
    /// </summary>
    [JsonPropertyName("lastSynced")]
    public DateTime? LastSynced { get; set; }

    /// <summary>
    /// Repo commit this PC last pulled or pushed. Pushing when the repo has moved past it would overwrite
    /// another PC's upload, so the push dialog warns.
    /// </summary>
    [JsonPropertyName("lastSyncedCommit")]
    public string? LastSyncedCommit { get; set; }

    /// <summary>GitHub repos ("owner/name") to check for addon updates, or null to use the ones the addons' .toc files name.</summary>
    [JsonPropertyName("addonSources")]
    public List<string>? AddonSources { get; set; }

    // Screen readers announce a list item by its ToString
    public override string ToString() => Name;

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

    /// <summary>The sync repo's newest commit as last seen this session, or null if it hasn't been checked.</summary>
    [JsonIgnore]
    public string? RemoteHead
    {
        get => _remoteHead;
        // The game list shows HasRemoteChanges, so it's told whenever this changes, wherever that happens
        set { _remoteHead = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasRemoteChanges))); }
    }
    private string? _remoteHead;

    [JsonIgnore]
    public bool HasRemoteChanges => RemoteHead != null && !string.Equals(RemoteHead, LastSyncedCommit, StringComparison.OrdinalIgnoreCase);

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
