using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace iscLauncher.Models;

/// <summary>A GitHub repo a game's addons are updated from.</summary>
public class AddonSource
{
    /// <summary>"owner/name".</summary>
    [JsonPropertyName("repo")]
    public string Repo { get; set; } = string.Empty;

    /// <summary>The installed addon folders it held when last checked, so they're listed under it before the next check.</summary>
    [JsonPropertyName("addons")]
    public List<string> Addons { get; set; } = [];

    /// <summary>
    /// The repo's version (release tag or commit) when the installed addons last matched it, and a fingerprint of their
    /// files then. While neither has changed, a check needn't download anything.
    /// </summary>
    [JsonPropertyName("matchedVersion")]
    public string? MatchedVersion { get; set; }

    [JsonPropertyName("matchedFiles")]
    public string? MatchedFiles { get; set; }
}

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

    [JsonPropertyName("realmlistAddress")]
    public string? RealmlistAddress { get; set; }

    [JsonPropertyName("accountName")]
    public string? AccountName { get; set; }

    [JsonPropertyName("realmName")]
    public string? RealmName { get; set; }

    /// <summary>Seconds to wait before typing the password; some clients take a while to show the login screen on a cold start.</summary>
    [JsonPropertyName("startupDelay")]
    public int StartupDelaySeconds { get; set; }

    [JsonPropertyName("syncRepoUrl")]
    public string? SyncRepoUrl { get; set; }

    /// <summary>Whether closing the game offers to push what changed while playing.</summary>
    [JsonPropertyName("offerPushOnClose")]
    public bool OfferPushOnClose { get; set; } = true;

    /// <summary>When this PC last pulled or pushed, in UTC.</summary>
    [JsonPropertyName("lastSynced")]
    public DateTime? LastSynced { get; set; }

    /// <summary>
    /// Repo commit this PC last pulled or pushed. Pushing when the repo has moved past it would overwrite
    /// another PC's upload, so the push dialog warns.
    /// </summary>
    [JsonPropertyName("lastSyncedCommit")]
    public string? LastSyncedCommit { get; set; }

    /// <summary>GitHub repos the addons are updated from. Repos an addon's .toc names are added as they're found.</summary>
    [JsonPropertyName("addonSources")]
    public List<AddonSource>? AddonSources { get; set; }

    /// <summary>Repos the user disconnected, which stay that way even though an addon's .toc names them.</summary>
    [JsonPropertyName("disconnectedAddonSources")]
    public List<string>? DisconnectedAddonSources { get; set; }

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

    /// <summary>One of the addons' repos has a version they haven't matched, as last seen this session.</summary>
    [JsonIgnore]
    public bool HasAddonUpdates
    {
        get => _hasAddonUpdates;
        set
        {
            if (_hasAddonUpdates == value) return;
            _hasAddonUpdates = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasAddonUpdates)));
        }
    }
    private bool _hasAddonUpdates;

    [JsonIgnore]
    public bool HasRemoteChanges => RemoteHead != null && !string.Equals(RemoteHead, LastSyncedCommit, StringComparison.OrdinalIgnoreCase);
}
