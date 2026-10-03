using Dalamud.Game;
using Dalamud.Game.Command;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Harmonia.Compatibility;
using Harmonia.Feeds;
using Harmonia.Game;
using Harmonia.Localization;
using Harmonia.Packs;
using Harmonia.Runtime;
using Harmonia.UI;

namespace Harmonia;

// Composition root. The pack chosen in the settings is verified and loaded
// once at startup; the row hooks exist only while a pack is loaded. Choosing
// another pack, updating the active one, or changing how it applies takes
// effect at the next game start.
public sealed class HarmoniaPlugin : IDalamudPlugin
{
    private const string CommandName = "/harmonia";
    private const string LocalizationDirName = "Localization";
    private const string FontCacheDirName = "font-cache";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IFramework framework;
    private readonly ICommandManager commands;
    private readonly IChatGui chat;
    private readonly INotificationManager notifications;
    private readonly Configuration configuration;
    private readonly SessionState session = new();
    private readonly TranslationPackStore packs;
    private readonly FeedUpdateService feeds;
    private readonly TranslationRuntime? runtime;
    private readonly ExcelRowHooks? hooks;
    private readonly TextCaseHooks? caseHooks;
    private readonly GameFonts? fonts;
    private readonly WindowSystem windows = new("Harmonia");
    private readonly MainWindow mainWindow;
    private readonly RestartWindow restartWindow;

    public HarmoniaPlugin(
        IDalamudPluginInterface pluginInterface,
        IFramework framework,
        ICommandManager commands,
        IChatGui chat,
        INotificationManager notifications,
        IPluginLog pluginLog,
        IGameInteropProvider interop,
        ISigScanner scanner,
        IClientState clientState,
        IDataManager dataManager)
    {
        this.pluginInterface = pluginInterface;
        this.framework = framework;
        this.commands = commands;
        this.chat = chat;
        this.notifications = notifications;
        var log = new HarmoniaLog(pluginLog);

        configuration = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        var pluginDir = pluginInterface.AssemblyLocation.Directory!.FullName;
        Lang.Initialize(Path.Combine(pluginDir, LocalizationDirName), configuration.Language, warning => pluginLog.Warning(warning));
        if (!string.Equals(configuration.Language, Lang.CurrentLanguage, StringComparison.OrdinalIgnoreCase))
        {
            configuration.Language = Lang.CurrentLanguage;
            Save();
        }

        var reloaded = configuration.SessionPid == Environment.ProcessId;
        if (reloaded)
        {
            session.IsRestartRequired = true;
            pluginLog.Warning("Harmonia was loaded again without a game restart; translation stays off until the game restarts.");
        }
        else
        {
            configuration.SessionPid = Environment.ProcessId;
            Save();
        }

        // Packs and the font cache live in the configuration directory:
        // Dalamud installs each version into its own folder and deletes the
        // old ones, so nothing kept next to the assembly survives an update.
        var dataDir = pluginInterface.ConfigDirectory.FullName;
        Directory.CreateDirectory(dataDir);
        PackStorageMigration.CopyLegacyPacks(pluginDir, Path.Combine(dataDir, TranslationPackStore.PacksDirName), log);

        var pluginVersion = pluginInterface.Manifest.AssemblyVersion.ToString();
        packs = new TranslationPackStore(
            dataDir,
            log,
            new FrameworkGameVersionProvider(),
            pluginVersion,
            ClientLanguageTag(clientState.ClientLanguage));
        packs.RemoveStaleFiles();
        var installer = new PackInstaller(packs, log);

        string? packError = null;
        string? hookError = null;
        string? compatibilityError = null;
        IReadOnlyList<CompatibilityProfile> compatibility = [];
        // Other plugins keep their settings next to Harmonia's own directory.
        var pluginConfigs = pluginInterface.ConfigDirectory.Parent?.FullName ?? dataDir;
        if (!reloaded && !string.IsNullOrEmpty(configuration.ActivePackId))
        {
            var selected = packs.TryGet(configuration.ActivePackId);
            var file = packs.OpenForRuntime(configuration.ActivePackId, out var openError);
            if (file is null)
            {
                packError = openError ?? selected?.InvalidReason ?? Lang.T("info.pack_not_usable");
            }
            else
            {
                fonts = new GameFonts(pluginInterface, framework, scanner, dataManager, file, Path.Combine(dataDir, FontCacheDirName), log);
                // Compatibility only ever keeps text: whatever fails in it
                // (another plugin's settings, the game data, a profile), the
                // translation applies as if no plugin were installed.
                KeptRows? keep = null;
                try
                {
                    // Plugins load after Harmonia, so being installed is what counts.
                    compatibility = RelevantProfiles(pluginInterface, pluginConfigs)
                        .Where(p => CompatibilityProfile.IsOn(p, configuration.DisabledCompatibility))
                        .ToList();
                    var sources = new CompatibilitySources(dataManager, log);
                    keep = CompatibilityProfile.Keep(compatibility, sources.Rows, c => c.Holds(pluginConfigs));
                    if (compatibility.Count > 0)
                        log.Info("Kept in the game's language for plugin compatibility: " + string.Join(", ", compatibility.Select(static p => p.Plugin)));
                }
                catch (Exception ex)
                {
                    compatibility = [];
                    keep = null;
                    compatibilityError = ex.Message;
                    log.Error("Plugin compatibility could not be prepared; everything is translated.", ex);
                }

                runtime = new TranslationRuntime(file, configuration.ActivePackId, selected?.FilePath ?? string.Empty,
                    new SheetFilter(configuration.UntranslatedSheets), keep);
                try
                {
                    hooks = new ExcelRowHooks(interop, scanner, runtime, log);
                }
                catch (Exception ex)
                {
                    hookError = ex.Message;
                    log.Error("Excel row hooks could not be installed; the game stays untranslated.", ex);
                }

                // The game capitalizes only Latin letters; a Cyrillic pack
                // needs its <head> and <caps> names capitalized too.
                if (hooks is not null && CyrillicCase.IsCyrillicLanguage(file.Manifest.Language))
                {
                    try
                    {
                        caseHooks = new TextCaseHooks(interop, scanner, log);
                    }
                    catch (Exception ex)
                    {
                        log.Error("Letter case hooks could not be installed; capitalized names stay lowercase.", ex);
                    }
                }
            }
        }

        var feedState = new FeedUpdateState();
        feeds = new FeedUpdateService(
            packs,
            installer,
            configuration,
            Save,
            feedState,
            session,
            log,
            (title, content) => notifications.AddNotification(new Notification
            {
                Title = title,
                Content = content,
                Type = NotificationType.Info,
                InitialDuration = TimeSpan.FromSeconds(10),
            }),
            pluginVersion,
            runtime?.Info.PackId);
        feeds.Start();

        mainWindow = new MainWindow(
            configuration,
            Save,
            packs,
            installer,
            feeds,
            feedState,
            session,
            new SessionInfo(runtime?.Info.PackId, runtime, hooks, packError, hookError, pluginVersion, fonts, reloaded, configuration.ActivePackId ?? string.Empty,
                [.. configuration.UntranslatedSheets], caseHooks, [.. compatibility.Select(static p => p.Plugin)], compatibilityError),
            pluginInterface.UiBuilder,
            () => RelevantProfiles(pluginInterface, pluginConfigs));
        restartWindow = new RestartWindow(() => commands.ProcessCommand("/xldisableplugintemp \"Harmonia\""))
        {
            IsOpen = reloaded,
        };
        windows.AddWindow(mainWindow);
        windows.AddWindow(restartWindow);

        commands.AddHandler(CommandName, new CommandInfo((_, _) => mainWindow.Toggle())
        {
            HelpMessage = Lang.T("command.help"),
        });
        pluginInterface.UiBuilder.Draw += windows.Draw;
        pluginInterface.UiBuilder.OpenMainUi += mainWindow.Toggle;
        pluginInterface.UiBuilder.OpenConfigUi += mainWindow.Toggle;
        framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        // Rows translated so far stay translated until the game frees them.
        if (hooks is not null && !Environment.HasShutdownStarted)
        {
            var message = Lang.T("notify.restart_needed");
            chat.Print(message, "Harmonia");
            notifications.AddNotification(new Notification
            {
                Title = "Harmonia",
                Content = message,
                Type = NotificationType.Warning,
                InitialDuration = TimeSpan.FromSeconds(10),
            });
        }

        framework.Update -= OnFrameworkUpdate;
        pluginInterface.UiBuilder.Draw -= windows.Draw;
        pluginInterface.UiBuilder.OpenMainUi -= mainWindow.Toggle;
        pluginInterface.UiBuilder.OpenConfigUi -= mainWindow.Toggle;
        windows.RemoveAllWindows();
        mainWindow.Dispose();
        commands.RemoveHandler(CommandName);

        feeds.Dispose();
        fonts?.Dispose();

        caseHooks?.Dispose();

        // The hooks wait for running detours before the pack is unmapped.
        hooks?.Dispose();
        runtime?.Dispose();
    }

    private void Save() => pluginInterface.SavePluginConfig(configuration);

    // Profiles of installed plugins that keep text with their current
    // settings; none when that cannot be told (the window asks every few
    // seconds and must keep drawing).
    private static IReadOnlyList<CompatibilityProfile> RelevantProfiles(IDalamudPluginInterface pluginInterface, string pluginConfigs)
    {
        try
        {
            return CompatibilityProfile.Installed(CompatibilityProfile.All, InstalledPluginNames(pluginInterface))
                .Where(p => p.KeepsAnything(c => c.Holds(pluginConfigs)))
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    // Both names: a plugin's manifest and its repository can disagree on the
    // internal one ("PandorasBox" and "Pandora's Box"). Harmonia loads before
    // Dalamud lists the other plugins, so the installed folders count too;
    // Dalamud's list adds the dev plugins.
    private static IEnumerable<string> InstalledPluginNames(IDalamudPluginInterface pluginInterface)
    {
        var launcherDir = pluginInterface.ConfigDirectory.Parent?.Parent?.FullName;
        var folders = launcherDir is null ? [] : InstalledPluginFolders.Names(Path.Combine(launcherDir, "installedPlugins"));
        return pluginInterface.InstalledPlugins
            .Where(static p => !p.IsBanned && !p.IsDecommissioned)
            .SelectMany(static p => new[] { p.InternalName, p.Name })
            .Concat(folders);
    }

    // The client may not report its version when the plugin loads; retry on
    // framework ticks (throttled by the store) until it is known.
    private void OnFrameworkUpdate(IFramework _)
    {
        if (packs.CurrentGameVersion is null)
            packs.RefreshGameVersion();

        if (packs.CurrentGameVersion is { } version)
        {
            framework.Update -= OnFrameworkUpdate;
            feeds.OnGameVersionKnown(version);
        }
    }

    // Pack source language tags of the client languages.
    private static string? ClientLanguageTag(ClientLanguage language) => language switch
    {
        ClientLanguage.Japanese => "ja",
        ClientLanguage.English => "en",
        ClientLanguage.German => "de",
        ClientLanguage.French => "fr",
        _ => null,
    };
}
