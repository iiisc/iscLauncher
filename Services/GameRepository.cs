using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using iscLauncher.Models;

namespace iscLauncher.Services;

/// <summary>
/// The game library: loaded once, kept in memory as the one list everything shares, and written back to games.json
/// after each change. Games are edited in place, then saved.
/// </summary>
public class GameRepository
{
    public static readonly string AppDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "IscLauncher");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _filePath;

    // A sync finishing while a game is being saved would otherwise have two writes share the temp file
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    /// <param name="filePath">Where the library lives; only tests pass one, to keep away from the real games.json.</param>
    public GameRepository(string? filePath = null) =>
        _filePath = filePath ?? Path.Combine(AppDataFolder, "games.json");

    public ObservableCollection<GameEntry> Games { get; } = new();

    public async Task LoadAsync()
    {
        if (!File.Exists(_filePath))
            return;

        var json = await File.ReadAllTextAsync(_filePath);
        GameLibrary library;
        try
        {
            library = JsonSerializer.Deserialize<GameLibrary>(json, JsonOptions) ?? new GameLibrary();
        }
        catch (JsonException ex)
        {
            // Move the unreadable file aside so the next save can't overwrite the user's only copy
            var corruptPath = $"{_filePath}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(_filePath, corruptPath);
            throw new InvalidDataException(
                $"games.json could not be read ({ex.Message}). It was moved to {corruptPath} and the library starts empty.", ex);
        }

        foreach (var game in library.Games)
            Games.Add(game);
    }

    public async Task AddAsync(GameEntry game)
    {
        Games.Add(game);
        await SaveAsync();
    }

    public async Task RemoveAsync(GameEntry game)
    {
        Games.Remove(game);
        await SaveAsync();
    }

    /// <summary>Writes the library as it is now.</summary>
    public async Task SaveAsync()
    {
        await _saveLock.WaitAsync();
        try
        {
            // Serialised once the lock is held, so the newest state is what lands on disk
            var json = JsonSerializer.Serialize(new GameLibrary { Games = [.. Games] }, JsonOptions);
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            // Write-then-rename so a crash mid-write can't leave a truncated games.json
            var tempPath = _filePath + ".tmp";
            await File.WriteAllTextAsync(tempPath, json);
            File.Move(tempPath, _filePath, overwrite: true);
        }
        finally
        {
            _saveLock.Release();
        }
    }
}
