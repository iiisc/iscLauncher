using System;
using System.Threading;
using System.Threading.Tasks;
using iscLauncher.Helpers;
using iscLauncher.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using static iscLauncher.Helpers.DialogHelper;

namespace iscLauncher.Dialogs;

/// <summary>GitHub's device sign-in: the user enters a short code on github.com, and the dialog closes once they approve.</summary>
public static class GitHubSignInDialog
{
    /// <summary>Returns the GitHub username, or null if the user closed the dialog. Throws if GitHub can't be reached.</summary>
    /// <param name="step">A heading for when signing in is one step of a longer setup.</param>
    public static async Task<string?> ShowAsync(XamlRoot xamlRoot, GitHubService gitHub, string? step = null)
    {
        using var cts = new CancellationTokenSource();
        var signIn = await gitHub.StartSignInAsync(cts.Token);

        var code = new TextBlock
        {
            Text = signIn.UserCode,
            FontSize = 28,
            FontWeight = FontWeights.SemiBold,
            CharacterSpacing = 200,
            Foreground = Res<Brush>("GoldBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            IsTextSelectionEnabled = true
        };
        var waiting = Dim("Waiting for you to approve iscLauncher on GitHub...");

        var panel = new StackPanel { Spacing = 14 };
        if (step != null)
            panel.Children.Add(SectionLabel(step));
        panel.Children.Add(BodyText($"Open {signIn.VerificationUri}, sign in if GitHub asks, and enter this code:"));
        panel.Children.Add(code);
        panel.Children.Add(waiting);
        panel.Children.Add(Dim("No GitHub account? Create a free one from GitHub's sign-in page. " +
            "GitHub may not bring you back to the code page afterwards, so click the button below again."));

        var dialog = CreateThemedDialog(xamlRoot, "Sign in with GitHub");
        dialog.Content = panel;
        dialog.PrimaryButtonText = "Copy Code and Open GitHub";
        // Opening the browser keeps the dialog up; it closes itself once GitHub reports the approval
        dialog.PrimaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            var package = new DataPackage();
            package.SetText(signIn.UserCode);
            Clipboard.SetContent(package);
            if (!Shell.Open(signIn.VerificationUri))
                waiting.Text = $"Couldn't open the browser. Go to {signIn.VerificationUri} yourself.";
        };

        string? login = null;
        async Task WaitForApprovalAsync()
        {
            try
            {
                // Creating a GitHub account can take longer than a code lasts, so an expired one is swapped for a new one
                while ((login = await gitHub.CompleteSignInAsync(signIn, cts.Token)) == null)
                {
                    signIn = await gitHub.StartSignInAsync(cts.Token);
                    code.Text = signIn.UserCode;
                    waiting.Text = "The code expired, so here's a new one. Still waiting for you to approve iscLauncher on GitHub...";
                }
                dialog.Hide();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { waiting.Text = ex.Message; }
        }

        var approval = WaitForApprovalAsync();
        await dialog.ShowAsync();
        cts.Cancel();
        await approval;
        return login;
    }
}
