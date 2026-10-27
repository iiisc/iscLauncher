using System;
using System.IO;
using iscLauncher.Helpers;
using Xunit;

namespace iscLauncher.Tests;

public sealed class ShortcutTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "isc-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public void AShortcutPointsAtTheAppAndStartsInItsFolder()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "iscLauncher.lnk");
        var target = Path.Combine(Environment.SystemDirectory, "notepad.exe");

        Shortcut.Create(path, target, Environment.SystemDirectory);

        // Read back through Windows' scripting shell, which this test may use since tests aren't trimmed
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        var link = shell.CreateShortcut(path);
        Assert.Equal(target, (string)link.TargetPath, ignoreCase: true);
        Assert.Equal(Environment.SystemDirectory, (string)link.WorkingDirectory, ignoreCase: true);
    }
}
