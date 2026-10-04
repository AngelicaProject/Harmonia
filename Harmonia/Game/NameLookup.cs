using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Harmonia.Dictionary;
using Harmonia.Localization;

namespace Harmonia.Game;

// Where players reach the name dictionary: /hfind prints matches into the
// chat log (items as links, so the game's own item menu works on them) or,
// without a name, opens the dictionary window; item menus copy the name in
// the game's language. Also the actions of the dictionary window.
internal sealed class NameLookup : IDisposable
{
    public const string FindCommand = "/hfind";

    private const string ChatTag = "Harmonia";
    private const int ChatMatches = 8;

    // UIColor row; without a prefix of its own the entry gets Dalamud's "D".
    private const ushort MenuPrefixColor = 52;

    private readonly NameDictionary dictionary;
    private readonly ICommandManager commands;
    private readonly IChatGui chat;
    private readonly IContextMenu contextMenu;
    private readonly IFramework framework;
    private readonly INotificationManager notifications;
    private readonly IHarmoniaLog log;
    private readonly Action openWindow;

    public NameLookup(
        NameDictionary dictionary,
        ICommandManager commands,
        IChatGui chat,
        IContextMenu contextMenu,
        IFramework framework,
        INotificationManager notifications,
        IHarmoniaLog log,
        Action openWindow)
    {
        this.dictionary = dictionary;
        this.commands = commands;
        this.chat = chat;
        this.contextMenu = contextMenu;
        this.framework = framework;
        this.notifications = notifications;
        this.log = log;
        this.openWindow = openWindow;

        commands.AddHandler(FindCommand, new CommandInfo(OnFind)
        {
            HelpMessage = Lang.T("command.find_help"),
        });
        contextMenu.OnMenuOpened += OnMenuOpened;
    }

    public void Dispose()
    {
        contextMenu.OnMenuOpened -= OnMenuOpened;
        commands.RemoveHandler(FindCommand);
    }

    private void OnFind(string command, string arguments)
    {
        var query = arguments.Trim();
        if (query.Length == 0)
        {
            openWindow();
            return;
        }

        if (NameText.Normalize(query).Length < NameIndex.MinQueryLength)
        {
            chat.Print(Lang.T("dictionary.usage"), ChatTag);
            return;
        }

        _ = FindAsync(query);
    }

    private async Task FindAsync(string query)
    {
        SeString message;
        try
        {
            message = Format(query, await dictionary.SearchAsync(query, ChatMatches).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }
        catch (Exception ex)
        {
            log.Error("Name search failed.", ex);
            message = Lang.T("dictionary.failed");
        }

        try
        {
            await framework.RunOnFrameworkThread(() => chat.Print(message, ChatTag)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.Warning("Name search results could not be printed: " + ex.Message);
        }
    }

    private static SeString Format(string query, NameSearchResult result)
    {
        var text = new SeStringBuilder();
        if (result.Total == 0)
            return text.AddText(Lang.T("dictionary.not_found", query)).Build();

        text.AddText(Lang.T("dictionary.found", query, result.Total));
        foreach (var entry in result.Matches)
        {
            text.Add(NewLinePayload.Payload).AddText("» ");
            if (entry.Category == NameCategory.Item)
                text.AddItemLink(entry.RowId, false, entry.Shown);
            else
                text.AddText(CategoryName(entry.Category) + ": " + entry.Shown);

            // The client language's name only when it differs from the shown one.
            var originals = entry.Originals
                .Select(static (o, i) => (Original: o, First: i == 0))
                .Where(o => !o.First || entry.IsTranslated)
                .Select(static o => o.First ? o.Original.Text : o.Original.Language.ToUpperInvariant() + ": " + o.Original.Text)
                .ToList();
            if (originals.Count > 0)
                text.AddText(" — " + string.Join(" · ", originals));
        }

        if (result.Total > result.Matches.Count)
            text.Add(NewLinePayload.Payload).AddText(Lang.T("dictionary.more", result.Total - result.Matches.Count, FindCommand));

        return text.Build();
    }

    public static string CategoryName(NameCategory category) => category switch
    {
        NameCategory.Item => Lang.T("dictionary.category_item"),
        NameCategory.Action => Lang.T("dictionary.category_action"),
        NameCategory.Status => Lang.T("dictionary.category_status"),
        NameCategory.Place => Lang.T("dictionary.category_place"),
        NameCategory.Duty => Lang.T("dictionary.category_duty"),
        NameCategory.Quest => Lang.T("dictionary.category_quest"),
        _ => category.ToString(),
    };

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        var itemId = ContextItem(args);
        if (itemId == 0 || dictionary.OriginalItemName(itemId) is not { } name)
            return;

        args.AddMenuItem(new MenuItem
        {
            Name = Lang.T("dictionary.copy_original"),
            PrefixChar = 'H',
            PrefixColor = MenuPrefixColor,
            OnClicked = _ => Copy(name),
        });
    }

    private static unsafe uint ContextItem(IMenuOpenedArgs args)
    {
        if (args.Target is MenuTargetInventory { TargetItem: { } item })
            return item.BaseItemId;

        // An item link in the chat log; a player's name there has a name or
        // a content id instead.
        if (args.AddonName == "ChatLog" && args.Target is MenuTargetDefault { TargetContentId: 0 } target &&
            string.IsNullOrEmpty(target.TargetName))
        {
            var agent = AgentChatLog.Instance();
            if (agent == null || agent->ContextItemId == 0)
                return 0;

            var (id, kind) = ItemUtil.GetBaseId(agent->ContextItemId);
            return kind == ItemKind.EventItem ? 0 : id;
        }

        return 0;
    }

    public void Copy(string name)
    {
        ImGui.SetClipboardText(name);
        Notify(Lang.T("dictionary.copied", name));
    }

    // The item as a link in the chat log, where the game's own item menu
    // links it to a channel.
    public void PrintLink(NameEntry item) =>
        Run(() => chat.Print(new SeStringBuilder().AddItemLink(item.RowId, false, item.Shown).Build(), ChatTag));

    public unsafe void TryOn(uint itemId) =>
        Run(() =>
        {
            var agent = AgentTryon.Instance();
            if (agent != null)
                AgentTryon.TryOn(0, itemId, 0, 0, 0, false);
        });

    public void OpenSite(string url) => Util.OpenLink(url);

    // Game functions run on the framework thread.
    private void Run(System.Action action) =>
        framework.RunOnFrameworkThread(action).ContinueWith(
            t => log.Warning("A dictionary action failed: " + t.Exception?.GetBaseException().Message),
            TaskContinuationOptions.OnlyOnFaulted);

    private void Notify(string text) =>
        notifications.AddNotification(new Notification
        {
            Title = "Harmonia",
            Content = text,
            Type = NotificationType.Success,
            InitialDuration = TimeSpan.FromSeconds(4),
        });
}
