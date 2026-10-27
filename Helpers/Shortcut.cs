using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace iscLauncher.Helpers;

/// <summary>Writes .lnk files through Windows' own shell link object, the only API .NET has for them.</summary>
internal static partial class Shortcut
{
    public static void Create(string path, string target, string workingDirectory)
    {
        Marshal.ThrowExceptionForHR(CoCreateInstance(ShellLinkClass, 0, InProcServer, ShellLinkInterface, out var instance));
        try
        {
            var link = (IShellLinkW)new StrategyBasedComWrappers().GetOrCreateObjectForComInstance(instance, CreateObjectFlags.UniqueInstance);
            link.SetPath(target);
            link.SetWorkingDirectory(workingDirectory);
            ((IPersistFile)link).Save(path, true);
        }
        finally
        {
            Marshal.Release(instance);
        }
    }

    private static readonly Guid ShellLinkClass = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid ShellLinkInterface = new("000214F9-0000-0000-C000-000000000046");
    private const uint InProcServer = 1;

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);

    // COM finds methods by their place in the interface, so the ones this doesn't call are listed just to hold their places
    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16), Guid("000214F9-0000-0000-C000-000000000046")]
    internal partial interface IShellLinkW
    {
        void GetPath();
        void GetIDList();
        void SetIDList();
        void GetDescription();
        void SetDescription();
        void GetWorkingDirectory();
        void SetWorkingDirectory(string dir);
        void GetArguments();
        void SetArguments();
        void GetHotkey();
        void SetHotkey();
        void GetShowCmd();
        void SetShowCmd();
        void GetIconLocation();
        void SetIconLocation();
        void SetRelativePath();
        void Resolve();
        void SetPath(string file);
    }

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16), Guid("0000010B-0000-0000-C000-000000000046")]
    internal partial interface IPersistFile
    {
        void GetClassID();
        void IsDirty();
        void Load();
        void Save(string fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);
    }
}
