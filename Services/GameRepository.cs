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

    internal static readonly string GamesFilePath = Path.Combine(AppDataFolder, "games.json");

    internal static readonly JsonSerializerOptions JsonOptions = new()
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

    public async Task<string> GetComputerNameAsync() => (await LoadAsync()).EffectiveComputerName;

    public Task SetComputerNameAsync(string name) =>
        MutateAsync(library => { library.ComputerName = string.IsNullOrWhiteSpace(name) ? null : name.Trim(); return true; });

    public Task SetCheckUpdatesOnStartupAsync(bool value) =>
        MutateAsync(library => { library.CheckUpdatesOnStartup = value; return true; });

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
