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
        UnhandledException += OnUnhandledException;
    }

    private async void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Log.Write($"Unhandled error: {e.Exception}");
        // Before the window exists there's nowhere to show it, so startup errors still close the app
        if (_window?.Content?.XamlRoot is not { } xamlRoot)
            return;

        // Kept running rather than vanishing, so the user can finish or restart on their own terms
        e.Handled = true;
        try
        {
            var dialog = DialogHelper.CreateThemedDialog(xamlRoot, "Something Went Wrong");
            dialog.Content = $"{e.Message}\n\nThe details are in the log. If anything looks wrong, restart the app.";
            dialog.PrimaryButtonText = "Open Log";
            dialog.CloseButtonText = "OK";
            if (await dialog.ShowAsync() == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
                Log.Open();
        }
        // Another dialog was already open; the error is still in the log
        catch { }
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
