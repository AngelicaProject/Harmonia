using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Harmonia.Fonts;
using Harmonia.Packs.Hpk;
using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;

namespace Harmonia.Game;

public enum GameFontsState
{
    // The active pack has no FONTS section.
    NotInPack,

    // Patched files are ready; Penumbra is not available to serve them.
    PenumbraMissing,

    // Penumbra serves the patched files through a temporary mod.
    Applied,

    // Penumbra serves them, but the game read its fonts before and the font
    // reload function was not recognized in this game build.
    NeedsReload,

    Failed,
}

// Adds the active pack's FONTS glyphs to the game's own fonts. The patched
// .fdt and .tex files are built once per (game version, pack) from the game's
// current files and served through a Penumbra temporary mod. Penumbra applies
// a temporary mod added outside a framework tick only on the next tick, after
// the game has read its fonts, so the game is then asked to read them again.
public sealed unsafe class GameFonts : IDisposable
{
    private const string ModTag = "Harmonia fonts";
    private const int ModPriority = 99;
    private const int PenumbraBreakingVersion = 5;

    // RaptureAtkModule virtual function that reads the game fonts again,
    // called as (module, lobby: false, reset: true) like Penumbra's "Reload
    // Fonts". It is called only when the signature finds exactly the function
    // in this slot: after a game patch that moved or changed it, fonts are
    // reported as needing a manual reload instead of calling something else.
    private const int ReloadFontsVFunc = 43;
    private const string ReloadFontsSignature =
        "48 89 5C 24 ?? 48 89 74 24 ?? 55 57 41 55 41 56 41 57 48 8D AC 24 ?? ?? ?? ?? 48 81 EC ?? ?? ?? ?? " +
        "48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 85 ?? ?? ?? ?? 44 88 44 24 ?? 0F B6 F2 88 54 24 ?? 4C 8B E9 " +
        "48 89 4C 24 ?? 45 84 C0";

    // Penumbra applies queued changes on its framework handler; the reload
    // must come after that.
    private const int ReloadDelayTicks = 10;
    private static readonly TimeSpan ReloadDelay = TimeSpan.FromSeconds(1);

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IFramework framework;
    private readonly ISigScanner scanner;
    private readonly IHarmoniaLog log;
    private readonly CancellationTokenSource reloadCts = new();
    private readonly FontCacheEntry? entry;
    private readonly IDisposable? initialized;
    private readonly IDisposable? disposed;
    private bool modAdded;

    public GameFonts(
        IDalamudPluginInterface pluginInterface,
        IFramework framework,
        ISigScanner scanner,
        IDataManager data,
        HpkFile pack,
        string cacheRoot,
        IHarmoniaLog log)
    {
        this.pluginInterface = pluginInterface;
        this.framework = framework;
        this.scanner = scanner;
        this.log = log;
        var cache = new FontCache(cacheRoot);
        if (!pack.HasFonts)
        {
            State = GameFontsState.NotInPack;
            cache.RemoveOthers(null);
            return;
        }

        try
        {
            var fonts = pack.ReadFonts()!;
            Sources = fonts.Sources;
            var version = data.GameData.Repositories.TryGetValue("ffxiv", out var repository) ? repository.Version : null;
            var key = FontCache.Key(version ?? "unknown", pack.PackHash);
            entry = cache.TryLoad(key);
            if (entry is null)
            {
                var result = FontPatcher.Patch(fonts, path => data.FileExists(path) ? data.GetFile(path)?.Data : null);
                entry = cache.Store(key, result);
                log.Info($"Prepared {result.Files.Count} font files for pack glyphs.");
            }

            cache.RemoveOthers(key);
            Reports = entry.Reports;
        }
        catch (Exception ex)
        {
            State = GameFontsState.Failed;
            Error = ex.Message;
            log.Error("Pack glyphs could not be added to the game fonts.", ex);
            return;
        }

        State = GameFontsState.PenumbraMissing;
        initialized = Initialized.Subscriber(pluginInterface, Apply);
        disposed = Disposed.Subscriber(pluginInterface, () =>
        {
            modAdded = false;
            State = GameFontsState.PenumbraMissing;
        });
        Apply();
    }

    public GameFontsState State { get; private set; }
    public string? Error { get; private set; }
    public IReadOnlyList<FontTargetReport> Reports { get; } = [];
    public IReadOnlyList<HpkFontSource> Sources { get; } = [];

    public void Dispose()
    {
        reloadCts.Cancel();
        reloadCts.Dispose();
        initialized?.Dispose();
        disposed?.Dispose();
        if (!modAdded)
            return;

        try
        {
            new RemoveTemporaryModAll(pluginInterface).Invoke(ModTag, ModPriority);
        }
        catch (Exception ex)
        {
            log.Warning("Could not remove the Penumbra font mod.", ex);
        }
    }

    private void Apply()
    {
        if (entry is null || modAdded)
            return;

        try
        {
            var (breaking, _) = new ApiVersion(pluginInterface).Invoke();
            if (breaking != PenumbraBreakingVersion)
            {
                Error = $"Penumbra API {breaking} is not supported.";
                return;
            }

            if (entry.Files.Count == 0)
            {
                State = GameFontsState.Applied;
                return;
            }

            var paths = entry.Files.ToDictionary(static file => file.Key.ToLowerInvariant(), static file => file.Value);
            var result = new AddTemporaryModAll(pluginInterface).Invoke(ModTag, paths, string.Empty, ModPriority);
            if (result != PenumbraApiEc.Success)
            {
                State = GameFontsState.Failed;
                Error = "Penumbra: " + result;
                return;
            }

            modAdded = true;
            Error = null;
            State = GameFontsState.Applied;
            _ = framework.RunOnTick(ReloadGameFonts, ReloadDelay, ReloadDelayTicks, reloadCts.Token);
        }
        catch (Exception ex)
        {
            // Penumbra is not installed or not loaded yet; Initialized retries.
            log.Debug("Penumbra is not available for fonts: " + ex.Message);
        }
    }

    // Runs on the framework thread. Without a UI module the game has read no
    // font yet and will read the patched files by itself.
    private void ReloadGameFonts()
    {
        try
        {
            var instance = Framework.Instance();
            var uiModule = instance == null ? null : instance->GetUIModule();
            var atkModule = uiModule == null ? null : uiModule->GetRaptureAtkModule();
            if (atkModule == null)
                return;

            var module = &atkModule->AtkModule;
            var slot = ((nint*)module->VirtualTable)[ReloadFontsVFunc];
            var text = scanner.Module.BaseAddress + (nint)scanner.TextSectionOffset;
            if (slot < text || slot >= text + scanner.TextSectionSize ||
                !scanner.TryScanText(ReloadFontsSignature, out var function) || function != slot)
            {
                State = GameFontsState.NeedsReload;
                log.Warning($"The font reload function was not recognized (slot {slot:X}); fonts need a manual reload.");
                return;
            }

            ((delegate* unmanaged<AtkModule*, bool, bool, void>)function)(module, false, true);
            log.Info("Reloaded the game fonts for pack glyphs.");
        }
        catch (Exception ex)
        {
            State = GameFontsState.NeedsReload;
            log.Warning("Could not reload the game fonts.", ex);
        }
    }
}
