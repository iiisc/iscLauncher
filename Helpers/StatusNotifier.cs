using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

namespace iscLauncher.Helpers;

/// <summary>Shows a message in an InfoBar; success messages auto-hide after a few seconds.</summary>
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
        _hideTimer.Stop();
        _text.Text = message;
        _bar.Severity = isSuccess ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        _bar.IsOpen = true;
        if (isSuccess)
            _hideTimer.Start();
    }
}
