using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using Harmonia.Feeds;
using Harmonia.Game;
using Harmonia.Localization;
using Harmonia.Packs;
using Harmonia.Packs.Hpk;

namespace Harmonia.UI;

// The Translations page: what the player sees first. It answers "is the
// translation on?" in one line and lists every installed pack together with
// its updates, so feeds never appear as a separate concept.
internal sealed partial class MainWindow
{
    private readonly HashSet<string> expanded = new(StringComparer.Ordinal);
    private readonly HashSet<string> changelogOpen = new(StringComparer.Ordinal);
    private readonly HashSet<string> fontsOpen = new(StringComparer.Ordinal);
    private readonly HashSet<string> licenseOpen = new(StringComparer.Ordinal);

    private string? feedSetupFor;
    private string feedSetupUrl = string.Empty;
    private string? feedSetupError;
    private bool feedSetupFocus;
    private Task<FeedDocument>? setupProbe;
    private string setupProbeUrl = string.Empty;

    private void DrawTranslations()
    {
        DrawHero();
        DrawNotices();

        Ui.Gap(6);
        DrawListHeader();
        Ui.Gap(4);

        var any = false;
        foreach (var pack in OrderedPacks())
        {
            DrawPackCard(pack, FeedFor(pack));
            any = true;
        }

        foreach (var url in configuration.UpdateFeedUrls.ToArray())
        {
            if (packs.FindByFeed(url) is not null)
                continue;

            DrawFeedCard(url, FeedStatusFor(url));
            any = true;
        }

        if (!any)
            DrawEmptyState();
    }

    // ---- Status ----

    private void DrawHero()
    {
        var (icon, color, title, text) = HeroState();
        var runtime = info.Runtime;
        var running = runtime is not null && info.Hooks is not null;

        using var card = Ui.BeginCard("hero");

        using (largeIconFont.Push())
        using (ImRaii.PushColor(ImGuiCol.Text, color))
            ImGui.TextUnformatted(icon.ToIconString());

        var textLeft = card.Left + LargeIconWidth(FontAwesomeIcon.CheckCircle) + (14 * Ui.Scale);
        var counterWidth = 0f;
        string? count = null;
        if (running)
        {
            count = runtime!.GetTotals().Applied.ToString("N0", CultureInfo.CurrentCulture);
            using (headingFont.Push())
                counterWidth = ImGui.CalcTextSize(count).X;
            counterWidth = Math.Max(counterWidth, ImGui.CalcTextSize(Lang.T("hero.lines_translated")).X);
        }

        Ui.SameLineAt(textLeft);
        using (ImRaii.Group())
        {
            using var wrap = ImRaii.TextWrapPos(card.Right - counterWidth - (16 * Ui.Scale));
            Ui.Heading(headingFont, title);
            Ui.Hint(text);
            if (HeroFailed && Ui.LinkButton(FontAwesomeIcon.Heartbeat, Lang.T("hero.show_details")))
                page = Page.Diagnostics;
        }

        if (count is null)
            return;

        Ui.SameLineAt(card.Right - counterWidth);
        using (ImRaii.Group())
        {
            using (headingFont.Push())
                ImGui.TextUnformatted(count);
            ImGui.TextDisabled(Lang.T("hero.lines_translated"));
        }
    }

    private bool HeroFailed =>
        !info.Reloaded &&
        (info.Runtime is null || info.Hooks is null) &&
        !string.IsNullOrEmpty(info.SelectedAtStart) &&
        string.Equals(configuration.ActivePackId, info.SelectedAtStart, StringComparison.Ordinal);

    private (FontAwesomeIcon Icon, Vector4 Color, string Title, string Text) HeroState()
    {
        if (info.Reloaded)
            return (FontAwesomeIcon.RedoAlt, Ui.Pending, Lang.T("hero.restart_title"), Lang.T("hero.reloaded_text"));

        if (info.Runtime is { } runtime && info.Hooks is not null)
        {
            var pack = packs.TryGet(runtime.Info.PackId);
            var languages = pack?.Manifest is { } m
                ? $" · {Ui.LanguageName(m.GameLanguage)} → {Ui.LanguageName(m.Language)}"
                : string.Empty;
            return (FontAwesomeIcon.CheckCircle, Ui.Good, Lang.T("hero.on_title"), runtime.Info.Title + languages);
        }

        if (HeroFailed)
            return (FontAwesomeIcon.TimesCircle, Ui.Bad, Lang.T("hero.failed_title"), Lang.T("hero.failed_text"));

        if (!string.IsNullOrEmpty(configuration.ActivePackId))
        {
            var name = packs.TryGet(configuration.ActivePackId)?.DisplayName ?? configuration.ActivePackId;
            return (FontAwesomeIcon.RedoAlt, Ui.Pending, Lang.T("hero.restart_title"), Lang.T("hero.turns_on_text", name));
        }

        return (FontAwesomeIcon.PowerOff, Ui.Muted, Lang.T("hero.off_title"),
            packs.Packs.Count > 0 ? Lang.T("hero.off_text") : Lang.T("hero.off_empty_text"));
    }

    private void DrawNotices()
    {
        var running = info.Runtime is not null && info.Hooks is not null;
        if (running && RestartPending)
        {
            string text;
            if (string.IsNullOrEmpty(configuration.ActivePackId))
                text = Lang.T("banner.turns_off");
            else if (!string.Equals(configuration.ActivePackId, info.LoadedPackId, StringComparison.Ordinal))
                text = Lang.T("banner.switch", packs.TryGet(configuration.ActivePackId)?.DisplayName ?? configuration.ActivePackId);
            else
                text = Lang.T("banner.restart");
            Ui.Notice("restart", FontAwesomeIcon.RedoAlt, Ui.Pending, text);
        }

        if (running && info.Fonts?.State == GameFontsState.PenumbraMissing)
            Ui.Notice("penumbra", FontAwesomeIcon.Font, Ui.Warn, Lang.T("notice.penumbra"));
        else if (running && info.Fonts?.State == GameFontsState.NeedsReload)
            Ui.Notice("fonts_reload", FontAwesomeIcon.Font, Ui.Warn, Lang.T("notice.fonts_reload"));

        if (staging is not null)
            Ui.Notice("staging", FontAwesomeIcon.HourglassHalf, Ui.Info, Lang.T("import.verifying"));

        if (importMessage is { } message && Ui.Notice("import", message.Failed ? FontAwesomeIcon.TimesCircle : FontAwesomeIcon.CheckCircle,
                message.Failed ? Ui.Bad : Ui.Good, message.Text, dismissible: true))
            importMessage = null;
    }

    private void DrawListHeader()
    {
        var right = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
        Ui.Heading(headingFont, Lang.T("translations.title"));

        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var checking = feedState.Checking;
        var checkLabel = checking ? Lang.T("translations.checking") : Lang.T("translations.check");
        var addLabel = Lang.T("translations.add");
        var hasFeeds = configuration.UpdateFeedUrls.Count > 0;
        var width = Ui.ButtonWidth(FontAwesomeIcon.Plus, addLabel) +
            (hasFeeds ? Ui.ButtonWidth(FontAwesomeIcon.SyncAlt, checkLabel) + spacing : 0);

        Ui.AlignRight(width, right, true);
        if (hasFeeds)
        {
            using (ImRaii.Disabled(checking))
            {
                if (Ui.Button(FontAwesomeIcon.SyncAlt, checkLabel))
                    _ = feeds.CheckNowAsync();
            }

            if (feedState.LastCheck is { } last)
                Ui.Tooltip(Lang.T("translations.last_check", last.ToString("g", CultureInfo.CurrentCulture)));
            ImGui.SameLine();
        }

        if (Ui.PrimaryButton(FontAwesomeIcon.Plus, addLabel))
            openAddPopup = true;
    }

    private void DrawEmptyState()
    {
        using var card = Ui.BeginCard("empty");
        Ui.Gap(8);
        Centered(card, () =>
        {
            using (largeIconFont.Push())
            using (ImRaii.PushColor(ImGuiCol.Text, Ui.Muted))
                ImGui.TextUnformatted(FontAwesomeIcon.Language.ToIconString());
        }, LargeIconWidth(FontAwesomeIcon.Language));
        Ui.Gap(4);
        Centered(card, () => Ui.Heading(headingFont, Lang.T("translations.empty_title")), HeadingWidth(Lang.T("translations.empty_title")));
        var text = Lang.T("translations.empty_text");
        Centered(card, () => ImGui.TextDisabled(text), Math.Min(ImGui.CalcTextSize(text).X, card.Width));
        Ui.Gap(6);
        var add = Lang.T("translations.add");
        Centered(card, () =>
        {
            if (Ui.PrimaryButton(FontAwesomeIcon.Plus, add))
                openAddPopup = true;
        }, Ui.ButtonWidth(FontAwesomeIcon.Plus, add));
        Ui.Gap(8);
    }

    private static void Centered(Ui.Card card, Action draw, float width)
    {
        ImGui.SetCursorPosX(MathF.Round(card.Left + Math.Max(0, (card.Width - width) / 2)));
        draw();
    }

    private float LargeIconWidth(FontAwesomeIcon icon)
    {
        using (largeIconFont.Push())
            return ImGui.CalcTextSize(icon.ToIconString()).X;
    }

    private float HeadingWidth(string text)
    {
        using (headingFont.Push())
            return ImGui.CalcTextSize(text).X;
    }

    // ---- Installed packs ----

    private IEnumerable<TranslationPack> OrderedPacks() =>
        packs.Packs
            .OrderByDescending(p => string.Equals(p.Id, configuration.ActivePackId, StringComparison.Ordinal))
            .ThenByDescending(p => string.Equals(p.Id, info.LoadedPackId, StringComparison.Ordinal))
            .ThenBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase);

    // The feed the translation updates from, while the player follows it.
    private (string Url, FeedStatus? Status)? FeedFor(TranslationPack pack)
    {
        var url = configuration.UpdateFeedUrls.FirstOrDefault(u => string.Equals(u, pack.FeedUrl, StringComparison.OrdinalIgnoreCase));
        return url is null ? null : (url, FeedStatusFor(url));
    }

    private FeedStatus? FeedStatusFor(string url) =>
        feedState.Feeds.FirstOrDefault(f => string.Equals(f.Url, url, StringComparison.OrdinalIgnoreCase));

    private void DrawPackCard(TranslationPack pack, (string Url, FeedStatus? Status)? link)
    {
        var feedUrl = link?.Url;
        var feed = link?.Status;
        var selected = string.Equals(pack.Id, configuration.ActivePackId, StringComparison.Ordinal);
        var started = !info.Reloaded && string.Equals(pack.Id, info.SelectedAtStart, StringComparison.Ordinal);
        var working = started && string.Equals(pack.Id, info.LoadedPackId, StringComparison.Ordinal) && info.Hooks is not null;
        var manifest = pack.Manifest;

        var installing = feedUrl is not null && (feed?.Status == FeedPackStatus.Downloading || installs.ContainsKey(feedUrl));
        var update = feed is { Status: FeedPackStatus.UpdateAvailable or FeedPackStatus.NeedsTrust } && !installing ? feed : null;

        using var card = Ui.BeginCard("pack_" + pack.Id);

        ImGui.TextUnformatted(pack.DisplayName);
        if (update is not null)
        {
            ImGui.SameLine();
            Ui.Badge(Lang.T("card.update_badge"), Ui.Info);
        }

        if (manifest?.Channel == "testing")
        {
            ImGui.SameLine();
            Ui.Badge(Lang.T("card.testing"), Ui.Muted);
        }

        if (manifest is not null)
            Ui.Hint($"{manifest.TeamName} · {Ui.LanguageName(manifest.GameLanguage)} → {Ui.LanguageName(manifest.Language)} · {Lang.T("card.version", manifest.Version)}");

        // Why it cannot be used, or what the player should know.
        if (manifest is null)
        {
            Ui.IconText(FontAwesomeIcon.TimesCircle, Ui.Bad, Lang.T("card.invalid"));
        }
        else
        {
            if (!pack.LanguageCompatible)
                Ui.IconText(FontAwesomeIcon.ExclamationTriangle, Ui.Warn,
                    Lang.T("card.wrong_language", Ui.LanguageName(manifest.GameLanguage), Ui.LanguageName(packs.ClientLanguage)));
            if (!pack.PluginCompatible)
                Ui.IconText(FontAwesomeIcon.ExclamationTriangle, Ui.Warn, Lang.T("card.old_plugin", manifest.MinHarmonia));
            if (pack.GameVersionMatches == false && pack.IsSelectable)
                Ui.Hint(Lang.T("card.game_drift"));
        }

        if (feed is not null)
            DrawFeedProgress(card, feed, installing, update is not null, Lang.T("card.update_available", feed.RemoteVersion ?? "?"), Lang.T("card.update_failed", feed.Error ?? "?"));

        // The switch shows what the player chose; the caption next to it says
        // what the game is doing now, since a change applies at the next start.
        Ui.Gap(4);
        var row = false;
        if (selected || pack.IsSelectable)
        {
            ImGui.AlignTextToFramePadding();
            if (Ui.Switch("##switch", selected))
                Select(selected ? string.Empty : pack.Id);
            Ui.Tooltip(Lang.T("card.restart_hint"));
            ImGui.SameLine();
            DrawPackState(selected, started, working);
            row = true;
        }

        if (update is not null)
        {
            if (row)
                ImGui.SameLine();
            if (Ui.PrimaryButton(FontAwesomeIcon.Download, Lang.T("card.update")))
                StartFeedInstall(update, pack.DisplayName);
            row = true;
        }

        var open = expanded.Contains(pack.Id);
        var more = open ? Lang.T("card.less") : Lang.T("card.more");
        var moreIcon = open ? FontAwesomeIcon.ChevronUp : FontAwesomeIcon.ChevronDown;
        Ui.AlignRight(Ui.ButtonWidth(moreIcon, more), card.Right, row);
        if (Ui.LinkButton(moreIcon, more) && !expanded.Remove(pack.Id))
            expanded.Add(pack.Id);

        if (open)
            DrawPackDetails(pack, feedUrl, feed, card.Right);
    }

    private void DrawPackState(bool selected, bool started, bool working)
    {
        switch (selected, started, working)
        {
            case (true, true, true) when session.IsRestartRequired:
                PendingState(Lang.T("card.updated_restart"));
                break;
            case (true, true, true):
                Ui.Colored(Ui.Good, Lang.T("card.on"));
                break;
            case (true, true, false):
                Ui.Colored(Ui.Bad, Lang.T("card.not_working"));
                break;
            case (true, false, _):
                PendingState(Lang.T("card.turns_on"));
                break;
            case (false, _, true):
                PendingState(Lang.T("card.turns_off"));
                break;
            default:
                ImGui.TextDisabled(Lang.T("card.off"));
                break;
        }
    }

    private static void PendingState(string text)
    {
        Ui.Icon(FontAwesomeIcon.RedoAlt, Ui.Pending);
        ImGui.SameLine();
        ImGui.TextUnformatted(text);
    }

    private void DrawPackDetails(TranslationPack pack, string? feedUrl, FeedStatus? feed, float right)
    {
        Ui.Gap(4);
        Ui.Divider(right);
        Ui.Gap(4);

        const float labelWidth = 190;
        if (pack.Manifest is { } manifest)
        {
            Ui.Row(Lang.T("details.team"), manifest.TeamName, labelWidth);
            if (manifest.Authors.Count > 0)
                Ui.Row(Lang.T("details.authors"), string.Join(", ", manifest.Authors), labelWidth);
            if (!string.IsNullOrWhiteSpace(manifest.TeamUrl))
                Ui.Row(Lang.T("details.website"), manifest.TeamUrl, labelWidth);
            if (!string.IsNullOrWhiteSpace(manifest.License))
                Ui.Row(Lang.T("details.license"), manifest.License, labelWidth);
            Ui.Row(Lang.T("details.version"), manifest.Channel == "testing"
                ? Lang.T("details.version_testing", manifest.Version)
                : manifest.Version, labelWidth);
            Ui.Row(Lang.T("details.game"), manifest.GameVersion, labelWidth);
            Ui.Row(Lang.T("details.key"), pack.PublisherFingerprint is { } fingerprint
                ? HpkSignature.ShortFingerprint(fingerprint)
                : Lang.T("details.unsigned"), labelWidth);
        }
        else
        {
            Ui.Row(Lang.T("details.reason"), pack.InvalidReason ?? "?", labelWidth);
        }

        Ui.Row(Lang.T("details.updates"), feedUrl is null ? Lang.T("details.no_updates")
            : feed is null ? Lang.T("feed.not_checked")
            : FeedStateText(feed), labelWidth);
        if (feedUrl is not null)
        {
            Ui.Row(string.Empty, feedUrl, labelWidth);
        }
        else if (feedSetupFor != pack.Id)
        {
            ImGui.SameLine();
            if (Ui.InlineLink(FontAwesomeIcon.Link, Lang.T("details.setup_updates")))
            {
                feedSetupFor = pack.Id;
                setupProbe = null;
                feedSetupUrl = string.Empty;
                feedSetupError = null;
                feedSetupFocus = true;
            }
        }
        else
        {
            DrawFeedSetup(pack, right);
        }

        DrawFontCredits(pack, labelWidth);

        Ui.Gap(4);
        if (Ui.LinkButton(FontAwesomeIcon.FolderOpen, Lang.T("details.open_folder")))
            Ui.OpenFolder(pack.PackDirectory);
        if (feedUrl is not null)
        {
            ImGui.SameLine();
            if (Ui.LinkButton(FontAwesomeIcon.Unlink, Lang.T("details.stop_updates")))
                feeds.RemoveFeed(feedUrl);
            Ui.Tooltip(Lang.T("details.stop_updates_hint"));
        }
    }

    // Connects a feed to a pack installed from a file. The link comes from
    // the translation's author; the pack itself does not carry it.
    private void DrawFeedSetup(TranslationPack pack, float right)
    {
        Ui.Gap(4);
        using var id = ImRaii.PushId("feed_setup");
        Ui.Hint(Lang.T("details.setup_text"));
        Ui.Gap(2);

        var save = Lang.T("details.setup_save");
        var cancel = Lang.T("common.cancel");
        var buttons = Ui.ButtonWidth(FontAwesomeIcon.Check, save) + ImGui.CalcTextSize(cancel).X +
            (ImGui.GetStyle().FramePadding.X * 2) + (ImGui.GetStyle().ItemSpacing.X * 2);
        ImGui.SetNextItemWidth(Math.Max(120 * Ui.Scale, right - ImGui.GetCursorPosX() - buttons));
        if (feedSetupFocus)
        {
            ImGui.SetKeyboardFocusHere();
            feedSetupFocus = false;
        }

        var probing = setupProbe is not null;
        var submit = false;
        using (ImRaii.Disabled(probing))
        {
            submit = ImGui.InputTextWithHint("##setup_url", "https://…", ref feedSetupUrl, 1024, ImGuiInputTextFlags.EnterReturnsTrue);
            ImGui.SameLine();
            using (ImRaii.Disabled(string.IsNullOrWhiteSpace(feedSetupUrl)))
                submit |= Ui.PrimaryButton(FontAwesomeIcon.Check, save);
        }

        ImGui.SameLine();
        if (ImGui.Button(cancel))
        {
            feedSetupFor = null;
            setupProbe = null;
            return;
        }

        // The link is saved only when its publisher key is the key this
        // translation trusts; anything else is another translation.
        if (submit && !probing && !string.IsNullOrWhiteSpace(feedSetupUrl))
        {
            var url = feedSetupUrl.Trim();
            feedSetupError = LinkProblem(url);
            if (feedSetupError is null)
            {
                setupProbeUrl = url;
                setupProbe = feeds.ProbeAsync(url);
            }
        }

        if (setupProbe is { IsCompleted: true } done)
        {
            setupProbe = null;
            if (!done.IsCompletedSuccessfully)
                feedSetupError = FeedErrors.Describe(done.Exception?.GetBaseException() ?? new TaskCanceledException());
            else if (pack.PinnedKey is null || !string.Equals(done.Result.PublisherKeyFingerprint, pack.PinnedKey, StringComparison.Ordinal))
                feedSetupError = Lang.T("link.other_pack", done.Result.Title ?? setupProbeUrl);
            else
                LinkFeed(pack, setupProbeUrl);
        }

        if (setupProbe is not null)
            Ui.IconText(FontAwesomeIcon.HourglassHalf, Ui.Muted, Lang.T("link.checking"));
        else if (feedSetupError is not null)
            Ui.IconText(FontAwesomeIcon.TimesCircle, Ui.Bad, feedSetupError);
    }

    private void LinkFeed(TranslationPack pack, string url)
    {
        try
        {
            packs.LinkFeed(pack.Id, url);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            feedSetupError = ex.Message;
            return;
        }

        if (feeds.AddFeed(url, out var errorKey))
            feedSetupFor = null;
        else
            feedSetupError = Lang.T(errorKey);
    }

    // Font attribution the licenses require: one row in the details, a
    // compact list when opened, and each distinct license text once.
    private void DrawFontCredits(TranslationPack pack, float labelWidth)
    {
        if (!string.Equals(pack.Id, info.LoadedPackId, StringComparison.Ordinal) || info.Fonts is not { Sources.Count: > 0 } fonts)
            return;

        var licenses = fonts.Sources.Select(static f => f.License).Distinct(StringComparer.Ordinal).ToList();
        Ui.Row(Lang.T("details.fonts"), $"{fonts.Sources.Count} · {string.Join(", ", licenses)}", labelWidth);
        ImGui.SameLine();
        var open = fontsOpen.Contains(pack.Id);
        if (Ui.InlineLink(open ? FontAwesomeIcon.ChevronUp : FontAwesomeIcon.ChevronDown, open ? Lang.T("card.less") : Lang.T("details.show")) &&
            !fontsOpen.Remove(pack.Id))
            fontsOpen.Add(pack.Id);

        if (!open)
            return;

        using var id = ImRaii.PushId("fonts");
        using var indent = ImRaii.PushIndent(labelWidth * Ui.Scale, false);
        Ui.Gap(2);
        foreach (var source in fonts.Sources)
        {
            ImGui.TextUnformatted(source.Family);
            Ui.Hint(CopyrightText(source.Copyright));
            Ui.Gap(2);
        }

        foreach (var license in fonts.Sources.GroupBy(static f => f.LicenseText, StringComparer.Ordinal))
        {
            var name = license.First().License;
            var key = pack.Id + "\n" + name;
            var shown = licenseOpen.Contains(key);
            if (Ui.InlineLink(shown ? FontAwesomeIcon.ChevronUp : FontAwesomeIcon.FileAlt, Lang.T("details.license_text", name)) &&
                !licenseOpen.Remove(key))
                licenseOpen.Add(key);
            if (shown)
                Ui.Hint(license.Key);
        }
    }

    // "Copyright 2015 The X Authors" reads as "© 2015 The X Authors".
    private static string CopyrightText(string copyright)
    {
        var text = copyright.Trim();
        return text.StartsWith("Copyright ", StringComparison.OrdinalIgnoreCase) ? "© " + text["Copyright ".Length..] : text;
    }

    private static string FeedStateText(FeedStatus feed) => feed.Status switch
    {
        FeedPackStatus.Checking => Lang.T("feed.checking"),
        FeedPackStatus.UpToDate => Lang.T("feed.up_to_date"),
        FeedPackStatus.UpdateAvailable or FeedPackStatus.NeedsTrust => Lang.T("feed.available", feed.RemoteVersion ?? "?"),
        FeedPackStatus.Downloading => Lang.T("feed.downloading"),
        FeedPackStatus.Incompatible => Lang.T("feed.incompatible"),
        FeedPackStatus.PluginTooOld => Lang.T("feed.plugin_too_old"),
        FeedPackStatus.Error => Lang.T("feed.error"),
        _ => Lang.T("feed.not_checked"),
    };

    // ---- Feeds whose pack is not installed yet ----

    private void DrawFeedCard(string url, FeedStatus? status)
    {
        using var card = Ui.BeginCard("feed_" + url);

        ImGui.TextUnformatted(status?.Title ?? Lang.T("feed.new_title"));
        ImGui.SameLine();
        Ui.Badge(Lang.T("card.not_installed"), Ui.Muted);
        Ui.Hint(url);

        var installing = status is not null && (status.Status == FeedPackStatus.Downloading || installs.ContainsKey(url));
        var available = status is { Status: FeedPackStatus.UpdateAvailable or FeedPackStatus.NeedsTrust } && !installing;
        switch (status?.Status)
        {
            case null or FeedPackStatus.Unknown:
                Ui.Hint(Lang.T("feed.not_checked"));
                break;
            case FeedPackStatus.Checking or FeedPackStatus.UpToDate:
                Ui.IconText(FontAwesomeIcon.HourglassHalf, Ui.Muted, Lang.T("feed.checking"));
                break;
            case FeedPackStatus.Incompatible:
                Ui.IconText(FontAwesomeIcon.ExclamationTriangle, Ui.Warn, Lang.T("feed.incompatible_long"));
                break;
            case FeedPackStatus.PluginTooOld:
                Ui.IconText(FontAwesomeIcon.ExclamationTriangle, Ui.Warn, Lang.T("feed.plugin_too_old_long"));
                break;
            default:
                DrawFeedProgress(card, status, installing, available,
                    Lang.T("feed.ready", status.RemoteVersion ?? "?"), Lang.T("feed.failed", status.Error ?? "?"));
                break;
        }

        Ui.Gap(4);
        if (available && Ui.PrimaryButton(FontAwesomeIcon.Download, Lang.T("card.install")))
            StartFeedInstall(status!, status!.Title ?? url);

        var remove = Lang.T("card.remove");
        Ui.AlignRight(Ui.ButtonWidth(FontAwesomeIcon.TrashAlt, remove), card.Right, available);
        using (ImRaii.Disabled(installing))
        {
            if (Ui.LinkButton(FontAwesomeIcon.TrashAlt, remove))
                feeds.RemoveFeed(url);
        }
    }

    // Download progress, an available release with its changelog, or the
    // last error of a feed.
    private void DrawFeedProgress(Ui.Card card, FeedStatus feed, bool installing, bool available, string availableText, string errorText)
    {
        if (installing)
        {
            ImGui.ProgressBar(feed.Progress, new Vector2(card.Width, 0), Lang.T("feed.downloading"));
            return;
        }

        if (feed.Status == FeedPackStatus.Error)
        {
            Ui.IconText(FontAwesomeIcon.TimesCircle, Ui.Bad, errorText);
            return;
        }

        if (!available)
            return;

        Ui.IconText(FontAwesomeIcon.ArrowCircleUp, Ui.Info, availableText);
        if (feed.Changelog is not { Length: > 0 } changelog)
            return;

        var open = changelogOpen.Contains(feed.Url);
        if (Ui.LinkButton(open ? FontAwesomeIcon.ChevronUp : FontAwesomeIcon.ChevronDown, Lang.T("card.whats_new")) && !changelogOpen.Remove(feed.Url))
            changelogOpen.Add(feed.Url);
        if (open)
        {
            using var indent = ImRaii.PushIndent();
            Ui.Hint(changelog);
        }
    }
}
