using System;
using System.Runtime.InteropServices;
using iscLauncher.Helpers;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using WinRT.Interop;

namespace iscLauncher;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        Environment.SetEnvironmentVariable("MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY", AppContext.BaseDirectory);
        InitializeComponent();
        UnhandledException += (_, e) => Log.Write($"Unhandled error: {e.Exception}");
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // One copy at a time: two would each keep their own view of games.json and overwrite each other's changes
        var main = AppInstance.FindOrRegisterForKey("main");
        if (!main.IsCurrent)
        {
            // Lets the running copy come to the front, which Windows only allows when the foreground app hands it over
            AllowSetForegroundWindow(main.ProcessId);
            await main.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs());
            Exit();
            return;
        }

        _window = new MainWindow();
        main.Activated += (_, _) => _window.DispatcherQueue.TryEnqueue(BringToFront);
        _window.Activate();
    }

    private void BringToFront()
    {
        if (_window!.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        _window.Activate();
        SetForegroundWindow(WindowNative.GetWindowHandle(_window));
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
