namespace Harmonia.Packs;

// Current game version ("7.35") for pack compatibility checks. Separated so
// the store stays testable without a running game.
public interface IGameVersionProvider
{
    string? GetCurrentGameVersion();
}

public sealed class FuncGameVersionProvider : IGameVersionProvider
{
    private readonly Func<string?> read;

    public FuncGameVersionProvider(Func<string?> read)
    {
        this.read = read ?? throw new ArgumentNullException(nameof(read));
    }

    public string? GetCurrentGameVersion()
    {
        try
        {
            return read();
        }
        catch
        {
            return null;
        }
    }
}

// Reads the version from the live game client. Never throws: null means
// unknown, and unknown means compatibility cannot be judged (packs load,
// UI warns).
public sealed class FrameworkGameVersionProvider : IGameVersionProvider
{
    public string? GetCurrentGameVersion()
    {
        try
        {
            unsafe
            {
                var framework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
                if (framework is null)
                    return null;

                var version = framework->GameVersionString;
                return string.IsNullOrWhiteSpace(version) ? null : version.Trim();
            }
        }
        catch
        {
            return null;
        }
    }
}
