using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

namespace iscLauncher.Helpers;

/// <summary>Shows a message in an InfoBar. Success messages auto-hide after a few seconds; progress and errors stay.</summary>
public sealed class StatusNotifier
{
    private readonly InfoBar _bar;
    private readonly TextBlock _text;
    private readonly DispatcherQueueTimer _hideTimer;

    public StatusNotifier(InfoBar bar, TextBlock text)
    {
        _bar = bar;
        _text = text;
        _hideTimer = bar.DispatcherQueue.CreateTimer();
        _hideTimer.Interval = TimeSpan.FromSeconds(5);
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += (_, _) => _bar.IsOpen = false;
    }

    public void Show(string message, bool isSuccess)
    {
        Log.Write(isSuccess ? message : $"Error: {message}");
        Show(message, isSuccess ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }

    /// <summary>Work in progress, or a reminder: stays up until the next message replaces it.</summary>
    public void ShowInfo(string message) => Show(message, InfoBarSeverity.Informational);

    private void Show(string message, InfoBarSeverity severity)
    {
        _hideTimer.Stop();
        _text.Text = message;
        _bar.Severity = severity;
        _bar.IsOpen = true;
        if (severity == InfoBarSeverity.Success)
            _hideTimer.Start();
    }
}
