using System;

namespace iscLauncher.Services;

/// <summary>The app's services, created once at startup and handed to each page.</summary>
/// <param name="OwnerHwnd">The main window, which file pickers need as their owner.</param>
public sealed record AppServices(
    GameRepository Library,
    CredentialService Credentials,
    GameLauncherService Launcher,
    AddonSyncService Sync,
    AddonUpdateService AddonUpdates,
    GitHubService GitHub,
    AppUpdateService Updates,
    IntPtr OwnerHwnd)
{
    public static AppServices Create(IntPtr ownerHwnd)
    {
        var library = new GameRepository();
        var credentials = new CredentialService();
        var gitHub = new GitHubService(credentials);
        return new AppServices(
            library,
            credentials,
            new GameLauncherService(credentials),
            new AddonSyncService(new GitService(() => gitHub.Token), library),
            new AddonUpdateService(gitHub),
            gitHub,
            new AppUpdateService(),
            ownerHwnd);
    }
}
