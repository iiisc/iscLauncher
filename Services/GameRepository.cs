using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using iscLauncher.Models;

namespace iscLauncher.Services;

public class GameRepository
{
    private static readonly string AppDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "IscLauncher");

    private static readonly string GamesFilePath = Path.Combine(AppDataFolder, "games.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _fileLock = new(1, 1);

    public async Task<GameLibrary> LoadAsync()
    {
        if (!File.Exists(GamesFilePath))
        {
            return new GameLibrary();
        }

        try
        {
            var json = await File.ReadAllTextAsync(GamesFilePath);
            return JsonSerializer.Deserialize<GameLibrary>(json, JsonOptions) ?? new GameLibrary();
        }
        catch
        {
            return new GameLibrary();
        }
    }

    public async Task SaveAsync(GameLibrary library)
    {
        Directory.CreateDirectory(AppDataFolder);
        var json = JsonSerializer.Serialize(library, JsonOptions);
        await File.WriteAllTextAsync(GamesFilePath, json);
    }

    public async Task AddGameAsync(GameEntry game)
    {
        await _fileLock.WaitAsync();
        try
        {
            var library = await LoadAsync();
            library.Games.Add(game);
            await SaveAsync(library);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task UpdateGameAsync(GameEntry game)
    {
        await _fileLock.WaitAsync();
        try
        {
            var library = await LoadAsync();
            var index = library.Games.FindIndex(g => g.Id == game.Id);
            if (index >= 0)
            {
                library.Games[index] = game;
                await SaveAsync(library);
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task RemoveGameAsync(Guid gameId)
    {
        await _fileLock.WaitAsync();
        try
        {
            var library = await LoadAsync();
            library.Games.RemoveAll(g => g.Id == gameId);
            await SaveAsync(library);
        }
        finally
        {
            _fileLock.Release();
        }
    }
}
