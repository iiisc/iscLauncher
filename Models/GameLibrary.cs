using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace iscLauncher.Models;

public class GameLibrary
{
    [JsonPropertyName("games")]
    public List<GameEntry> Games { get; set; } = new();

    /// <summary>
    /// Friendly name for this computer, shown in sync commit messages.
    /// Falls back to <see cref="System.Environment.MachineName"/> when empty.
    /// </summary>
    [JsonPropertyName("computerName")]
    public string? ComputerName { get; set; }
}
