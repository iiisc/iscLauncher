using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using iscLauncher.Models;

namespace iscLauncher.Services;

public class GameRepository
{
    public static readonly string AppDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "IscLauncher");

    private static readonly string GamesFilePath = Path.Combine(AppDataFolder, "games.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static readonly SemaphoreSlim _fileLock = new(1, 1);

    public async Task<GameLibrary> LoadAsync()
    {
        if (!File.Exists(GamesFilePath))
        {
            return new GameLibrary();
        }

        var json = await File.ReadAllTextAsync(GamesFilePath);
        try
        {
            return JsonSerializer.Deserialize<GameLibrary>(json, JsonOptions) ?? new GameLibrary();
        }
        catch (JsonException ex)
        {
            // Move the unreadable file aside so the next save can't overwrite the user's only copy
            var corruptPath = $"{GamesFilePath}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(GamesFilePath, corruptPath);
            throw new InvalidDataException(
                $"games.json could not be read ({ex.Message}). It was moved to {corruptPath} and the library starts empty.", ex);
        }
    }

    private async Task SaveAsync(GameLibrary library)
    {
        Directory.CreateDirectory(AppDataFolder);
        var json = JsonSerializer.Serialize(library, JsonOptions);
        // Write-then-rename so a crash mid-write can't leave a truncated games.json
        var tempPath = GamesFilePath + ".tmp";
        await File.WriteAllTextAsync(tempPath, json);
        File.Move(tempPath, GamesFilePath, overwrite: true);
    }

    public Task AddGameAsync(GameEntry game) =>
        MutateAsync(library => { library.Games.Add(game); return true; });

    public Task UpdateGameAsync(GameEntry game) =>
        MutateAsync(library =>
        {
            var index = library.Games.FindIndex(g => g.Id == game.Id);
            if (index < 0)
                return false;
            library.Games[index] = game;
            return true;
        });

    public Task RemoveGameAsync(Guid gameId) =>
        MutateAsync(library => { library.Games.RemoveAll(g => g.Id == gameId); return true; });

    /// <summary>Load, apply <paramref name="change"/>, and save only if it returns true.</summary>
    private async Task MutateAsync(Func<GameLibrary, bool> change)
    {
        await _fileLock.WaitAsync();
        try
        {
            var library = await LoadAsync();
            if (change(library))
                await SaveAsync(library);
        }
        finally
        {
            _fileLock.Release();
        }
    }
}
