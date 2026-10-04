using System.Runtime.InteropServices;

namespace Harmonia;

// Whether the game runs on Windows or under Wine (Proton, XIVLauncher.Core).
// Wine exports wine_get_version from ntdll; Windows does not.
public static unsafe class Platform
{
    private static readonly Lazy<string?> WineVersion = new(ReadWineVersion);
    private static readonly Lazy<string?> WineHost = new(ReadWineHost);

    public static bool IsWine => WineVersion.Value is not null;

    // "Windows 10.0.26200", "Wine 9.22 on Linux 6.17.1".
    public static string Describe()
    {
        if (WineVersion.Value is not { } version)
            return "Windows " + Environment.OSVersion.Version;

        return WineHost.Value is { } host ? $"Wine {version} on {host}" : $"Wine {version}";
    }

    private static string? ReadWineVersion()
    {
        try
        {
            if (!TryGetNtdllExport("wine_get_version", out var export))
                return null;

            return Marshal.PtrToStringUTF8((nint)((delegate* unmanaged<byte*>)export)()) ?? string.Empty;
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadWineHost()
    {
        try
        {
            if (!TryGetNtdllExport("wine_get_host_version", out var export))
                return null;

            byte* system = null;
            byte* release = null;
            ((delegate* unmanaged<byte**, byte**, void>)export)(&system, &release);
            var name = Marshal.PtrToStringUTF8((nint)system);
            return name is null ? null : $"{name} {Marshal.PtrToStringUTF8((nint)release)}".TrimEnd();
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetNtdllExport(string name, out nint export)
    {
        export = 0;
        return NativeLibrary.TryLoad("ntdll.dll", out var ntdll) && NativeLibrary.TryGetExport(ntdll, name, out export);
    }
}
