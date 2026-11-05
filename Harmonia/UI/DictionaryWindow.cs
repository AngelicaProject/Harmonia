using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Harmonia.Dictionary;
using Harmonia.Game;
using Harmonia.Localization;

namespace Harmonia.UI;

// The dictionary overlay shares one search snapshot and per-name actions.
internal sealed partial class DictionaryWindow : Window, IDisposable
{
    private const int PaletteMatches = 6;
    private const string RowMenu = "##row_menu";
    private static readonly TimeSpan CopiedFeedback = TimeSpan.FromSeconds(2);
    // A search takes a few milliseconds; the pause only skips the letters
    // of a word typed in one go.
    private static readonly TimeSpan DictionarySearchDelay = TimeSpan.FromMilliseconds(120);

    private readonly NameDictionary dictionary;
    private readonly NameLookup lookup;
    private readonly ITextureProvider textures;
    private readonly Configuration configuration;
    private readonly Action save;
    private readonly DictionaryKeyboard keyboard;
    private readonly Action openSettings;
    private int navigationDelta;
    private int selectedResult;
    private bool scrollToSelected;

    private string dictionaryQuery;
    private bool queryChanged;
    private NameCategory? dictionaryCategory = null;
    private DateTime dictionaryEdited;
    private (string Query, NameCategory? Category)? dictionarySearched;
    private Task<DictionaryResults>? dictionarySearch;
    private CancellationTokenSource? dictionaryCancel;
    private DictionaryResults? dictionaryResults;
    private NameCategory? dictionaryResultCategory;
    private bool resetDictionaryScroll;

    private bool DictionaryResultsCurrent => dictionaryResults is { } results &&
        results.Query == dictionaryQuery.Trim() && dictionaryResultCategory == dictionaryCategory;
    private bool dictionaryFailed;
    private bool focusDictionary;
    private bool dictionaryInputActive;
    private bool ignoreTabNavigation;
    private string? dictionaryCopiedText;
    private DateTime dictionaryCopiedAt;

    private sealed record DictionaryResults(string Query, NameSearchResult Result, NameDetails[] Details);

    public DictionaryWindow(NameDictionary dictionary, NameLookup lookup, ITextureProvider textures,
        Configuration configuration, Action save, DictionaryKeyboard keyboard,
        Action openSettings)
        : base(Lang.T("dictionary.window_title") + "###harmonia_palette")
    {
        this.dictionary = dictionary;
        this.lookup = lookup;
        this.textures = textures;
        this.configuration = configuration;
        this.save = save;
        this.keyboard = keyboard;
        this.openSettings = openSettings;
        Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings;
        DisableWindowSounds = true;
        RespectCloseHotkey = false;
        dictionaryQuery = configuration.DictionaryClearSearchOnOpen ? string.Empty : configuration.DictionaryQuery;
        Size = new Vector2(520, 440);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(340, 240),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void Open(string? query = null)
    {
        dictionaryQuery = query ?? (configuration.DictionaryClearSearchOnOpen ? string.Empty : configuration.DictionaryQuery);
        if (configuration.DictionaryClearSearchOnOpen)
        {
            dictionaryCancel?.Cancel();
            dictionarySearch = null;
            dictionaryResults = null;
            dictionaryFailed = false;
            selectedResult = 0;
            resetDictionaryScroll = true;
            queryChanged = false;
        }
        dictionarySearched = null;
        IsOpen = true;
        focusDictionary = true;
        BringToFront();
    }

    public void TogglePalette()
    {
        if (IsOpen)
            IsOpen = false;
        else
            Open();
    }

    public override void OnOpen() => focusDictionary = !arranging;

    public override void OnClose()
    {
        arranging = false;
        SaveQuery();
    }

    private void SaveQuery()
    {
        if (!queryChanged)
            return;
        queryChanged = false;
        var query = configuration.DictionaryClearSearchOnOpen ? string.Empty : dictionaryQuery;
        if (configuration.DictionaryQuery == query)
            return;
        configuration.DictionaryQuery = query;
        save();
    }

    public void Dispose()
    {
        SaveQuery();
        dictionaryCancel?.Cancel();
        dictionaryCancel?.Dispose();
    }

    public override void Draw()
    {
        WindowName = Lang.T("dictionary.window_title") + "###harmonia_palette";
        DrawPalette();
    }

    private int NavigateHistory(ref ImGuiInputTextCallbackData data)
    {
        if (data.EventFlag == ImGuiInputTextFlags.CallbackHistory)
            navigationDelta += data.EventKey == ImGuiKey.DownArrow ? 1 : -1;
        else if (data.EventFlag == ImGuiInputTextFlags.CallbackCompletion)
        {
            var io = ImGui.GetIO();
            if (!ignoreTabNavigation && !io.KeyShift && !io.KeyCtrl && !io.KeyAlt && dictionaryResults is { Result.Matches.Count: > 0 } results)
                navigationDelta += selectedResult == results.Result.Matches.Count - 1 ? -selectedResult : 1;
        }
        return 0;
    }

    private void CopyOriginal(NameEntry entry)
    {
        lookup.Copy(entry.Originals.Count > 0 ? entry.Originals[0].Text : entry.Shown);
        SaveQuery();
        IsOpen = false;
    }

    private static string FitName(string text, float width)
    {
        if (ImGui.CalcTextSize(text).X <= width)
            return text;
        var elements = System.Globalization.StringInfo.ParseCombiningCharacters(text);
        var length = elements.Length;
        while (length > 0)
        {
            var candidate = text[..elements[--length]] + "…";
            if (ImGui.CalcTextSize(candidate).X <= width)
                return candidate;
        }
        return "…";
    }

    // Keep the displayed snapshot until the latest search is ready. Publishing
    // never exposes a partial list or invalidates a menu the player is using.
    private void PollDictionary(NameDictionary dictionary, bool popupOpen)
    {
        if (popupOpen)
            return;

        var wanted = (Query: dictionaryQuery.Trim(), Category: dictionaryCategory);
        if (dictionarySearch is { IsCompleted: true } done)
        {
            if (dictionarySearched == wanted)
            {
                dictionaryFailed = done.IsFaulted;
                if (done.IsCompletedSuccessfully)
                {
                    var next = done.Result;
                    var sameRows = dictionaryResults is { } previous &&
                        previous.Result.Matches.Select(static e => (e.Category, e.RowId))
                            .SequenceEqual(next.Result.Matches.Select(static e => (e.Category, e.RowId)));
                    dictionaryResults = next;
                    dictionaryResultCategory = wanted.Category;
                    if (!sameRows)
                    {
                        selectedResult = 0;
                        scrollToSelected = false;
                        resetDictionaryScroll = true;
                    }
                }
            }
            dictionarySearch = null;
        }

        if (wanted == dictionarySearched &&
            (dictionarySearch is not null || DictionaryResultsCurrent || dictionaryFailed))
            return;

        dictionaryFailed = false;
        if (NameText.Normalize(wanted.Query).Length < NameIndex.MinQueryLength)
        {
            dictionaryCancel?.Cancel();
            dictionarySearch = null;
            dictionarySearched = wanted;
            dictionaryResults = null;
            return;
        }
        if (DateTime.UtcNow - dictionaryEdited < DictionarySearchDelay)
            return;

        dictionarySearched = wanted;
        dictionaryCancel?.Cancel();
        dictionaryCancel?.Dispose();
        dictionaryCancel = new CancellationTokenSource();
        dictionarySearch = SearchDictionaryAsync(dictionary, wanted.Query, wanted.Category, PaletteMatches, dictionaryCancel.Token);
    }

    private static async Task<DictionaryResults> SearchDictionaryAsync(
        NameDictionary dictionary, string query, NameCategory? category, int limit, CancellationToken cancellationToken)
    {
        var result = await dictionary.SearchAsync(query, limit, category, cancellationToken).ConfigureAwait(false);
        var details = result.Matches.Select(dictionary.Describe).ToArray();
        return new DictionaryResults(query, result, details);
    }

    private static void DrawDictionaryMenu(NameEntry entry, NameDetails details, NameLookup lookup)
    {
        if (ImGui.Selectable(Lang.T("dictionary.copy_shown", entry.Shown)))
            lookup.Copy(entry.Shown);
        foreach (var (original, i) in entry.Originals.Select(static (o, i) => (o, i)))
        {
            if (i == 0 && !entry.IsTranslated)
                continue;
            if (ImGui.Selectable(Lang.T("dictionary.copy_named", Ui.LanguageName(original.Language), original.Text)))
                lookup.Copy(original.Text);
        }

        if (entry.Category == NameCategory.Item)
        {
            ImGui.Separator();
            if (ImGui.Selectable(Lang.T("dictionary.show_link")))
                lookup.PrintLink(entry);
            if (details.CanTryOn && ImGui.Selectable(Lang.T("dictionary.try_on")))
                lookup.TryOn(entry.RowId);
        }

        var sites = NameSites.For(entry, details.Marketable);
        if (sites.Count == 0)
            return;

        ImGui.Separator();
        ImGui.TextDisabled(Lang.T("dictionary.open_site"));
        foreach (var site in sites)
        {
            if (ImGui.Selectable(site.Name))
                lookup.OpenSite(site.Url);
        }
    }

}
