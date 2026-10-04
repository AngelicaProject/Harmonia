using System.Runtime.InteropServices;
using Dalamud.Utility;

namespace Harmonia;

// Windows, or Wine and the system it runs on, as Dalamud's start info records
// it from the launcher. Probing ntdll is not enough: a Wine prefix can hide
// the wine_* exports. wine_get_version only adds the version when exported.
// Dalamud's Util waits for Dalamud to be running, so call this from the UI.
public static unsafe class Platform
{
    private static readonly Lazy<string> Description = new(Read);

    // "Windows 10.0.26200", "Wine 9.22 on Linux", "Wine on macOS".
    public static string Describe() => Description.Value;

    private static string Read()
    {
        bool wine;
        OSPlatform host;
        try
        {
            wine = Util.IsWine();
            host = Util.GetHostPlatform();
        }
        catch
        {
            wine = WineVersion() is not null;
            host = OSPlatform.Windows;
        }

        if (!wine)
            return "Windows " + Environment.OSVersion.Version;

        var version = WineVersion();
        var name = version is null ? "Wine" : "Wine " + version;
        if (host == OSPlatform.Linux)
            return name + " on Linux";
        if (host == OSPlatform.OSX)
            return name + " on macOS";
        return name;
    }

    private static string? WineVersion()
    {
        try
        {
            if (!NativeLibrary.TryLoad("ntdll.dll", out var ntdll) ||
                !NativeLibrary.TryGetExport(ntdll, "wine_get_version", out var export))
                return null;

            return Marshal.PtrToStringUTF8((nint)((delegate* unmanaged<byte*>)export)());
        }
        catch
        {
            return null;
        }
    }
}
