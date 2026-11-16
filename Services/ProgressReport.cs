using System.Linq;

namespace iscLauncher.Services;

/// <param name="Fraction">How far along, from 0 to 1, or null when it can't be told.</param>
public record ProgressReport(string Text, double? Fraction = null)
{
    public static implicit operator ProgressReport(string text) => new(text);

    /// <summary>The one way every download and upload is described: "Downloading... 45% · 12.30 MB · 5.20 MB/s".</summary>
    public static ProgressReport Transfer(string action, int percent, string? amount = null, string? speed = null) =>
        new(string.Join(" · ", new[] { $"{action}... {percent}%", amount, speed }.OfType<string>()), percent / 100.0);

    // Counted in 1024s, as git does, so the app's own downloads read the same as git's
    public static string Size(double bytes) =>
        bytes >= 1 << 30 ? $"{bytes / (1 << 30):0.00} GB"
        : bytes >= 1 << 20 ? $"{bytes / (1 << 20):0.00} MB"
        : $"{bytes / 1024:0.00} KB";
}
