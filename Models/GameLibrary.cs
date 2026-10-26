using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace iscLauncher.Models;

public class GameLibrary
{
    [JsonPropertyName("games")]
    public List<GameEntry> Games { get; set; } = new();
}
