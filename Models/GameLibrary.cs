using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace iscLauncher.Models;

public class GameLibrary
{
    [JsonPropertyName("games")]
    public List<GameEntry> Games { get; set; } = new();

    /// <summary>
    /// Friendly name for this computer, shown in sync commit messages.
    /// Use <see cref="EffectiveComputerName"/> to read it with the machine-name fallback applied.
    /// </summary>
    [JsonPropertyName("computerName")]
    public string? ComputerName { get; set; }

    [JsonIgnore]
    public string EffectiveComputerName =>
        string.IsNullOrWhiteSpace(ComputerName) ? Environment.MachineName : ComputerName;

    [JsonPropertyName("checkUpdatesOnStartup")]
    public bool CheckUpdatesOnStartup { get; set; }
}
