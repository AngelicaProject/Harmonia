using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Harmonia.Feeds;
using Harmonia.Localization;
using Harmonia.Packs;
using Harmonia.Packs.Hpk;

namespace Harmonia.UI;

// Adding translations: the "Add translation" dialog, file imports, feed
// installs, and the confirmation of a publisher key the player has not
// trusted yet.
internal sealed partial class MainWindow
{
    private const string AddPopupId = "###harmonia_add";
    private const string TrustPopupId = "###harmonia_trust";

    private readonly Dictionary<string, Task<bool>> installs = new(StringComparer.OrdinalIgnoreCase);

    // Links added in the dialog: installed as soon as their check finishes.
    private readonly HashSet<string> autoInstall = new(StringComparer.OrdinalIgnoreCase);

    private Task<StagedPack>? staging;
    private StagedPack? pendingImport;
    private FeedTrust? pendingFeedTrust;
    private bool openTrustPopup;
    private bool openAddPopup;
    private (string Text, bool Failed)? importMessage;
    private string newFeedUrl = string.Empty;
    private string? addError;
    private Task<FeedDocument>? addProbe;
    private string addProbeUrl = string.Empty;

    private sealed record FeedTrust(string Url, string Title, string Fingerprint);

    // ---- Add dialog ----

    private void DrawAddPopup()
    {
        var title = Lang.T("add.title") + AddPopupId;
        if (openAddPopup)
        {
            ImGui.OpenPopup(title);
            openAddPopup = false;
            addError = null;
        }

        ImGui.SetNextWindowSize(new Vector2(500 * Ui.Scale, 0), ImGuiCond.Appearing);
        using var popup = ImRaii.PopupModal(title, ImGuiWindowFlags.NoResize);
        if (!popup)
            return;

        using var wrap = ImRaii.TextWrapPos(0);

        Ui.Icon(FontAwesomeIcon.Link);
        ImGui.SameLine();
        ImGui.TextUnformatted(Lang.T("add.link_title"));
        Ui.Hint(Lang.T("add.link_text"));
        Ui.Gap(2);

        var addLabel = Lang.T("add.link_button");
        var buttonWidth = Ui.ButtonWidth(FontAwesomeIcon.Plus, addLabel);
        var probing = addProbe is not null;
        var submit = false;
        using (ImRaii.Disabled(probing))
        {
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - buttonWidth - ImGui.GetStyle().ItemSpacing.X);
            submit = ImGui.InputTextWithHint("##feed_url", "https://…", ref newFeedUrl, 1024, ImGuiInputTextFlags.EnterReturnsTrue);
            ImGui.SameLine();
            using (ImRaii.Disabled(string.IsNullOrWhiteSpace(newFeedUrl)))
                submit |= Ui.PrimaryButton(FontAwesomeIcon.Plus, addLabel);
        }

        if (submit && !probing && !string.IsNullOrWhiteSpace(newFeedUrl))
        {
            var url = newFeedUrl.Trim();
            addError = LinkProblem(url);
            if (addError is null)
            {
                addProbeUrl = url;
                addProbe = feeds.ProbeAsync(url);
            }
        }

        // The link is saved only once it proved to be a feed.
        if (addProbe is { IsCompleted: true } done)
        {
            addProbe = null;
            if (!done.IsCompletedSuccessfully)
            {
                addError = FeedErrors.Describe(done.Exception?.GetBaseException() ?? new TaskCanceledException());
            }
            else if (feeds.AddFeed(addProbeUrl, out var errorKey))
            {
                autoInstall.Add(addProbeUrl);
                newFeedUrl = string.Empty;
                ImGui.CloseCurrentPopup();
            }
            else
            {
                addError = Lang.T(errorKey);
            }
        }

        if (addProbe is not null)
            Ui.IconText(FontAwesomeIcon.HourglassHalf, Ui.Muted, Lang.T("link.checking"));
        else if (addError is not null)
            Ui.IconText(FontAwesomeIcon.TimesCircle, Ui.Bad, addError);

        Ui.Gap(8);
        Ui.Divider(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
        Ui.Gap(8);

        Ui.Icon(FontAwesomeIcon.FileImport);
        ImGui.SameLine();
        ImGui.TextUnformatted(Lang.T("add.file_title"));
        Ui.Hint(Lang.T("add.file_text"));
        Ui.Gap(2);
        using (ImRaii.Disabled(staging is not null))
        {
            if (Ui.Button(FontAwesomeIcon.FolderOpen, Lang.T("add.file_button")))
            {
                ImGui.CloseCurrentPopup();
                OpenImportDialog();
            }
        }

        Ui.Gap(8);
        var cancel = Lang.T("common.cancel");
        Ui.AlignRight(ImGui.CalcTextSize(cancel).X + (ImGui.GetStyle().FramePadding.X * 2), ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X, false);
        if (ImGui.Button(cancel))
        {
            addProbe = null;
            ImGui.CloseCurrentPopup();
        }
    }

    // Null when the link is worth checking; otherwise why it is not.
    private string? LinkProblem(string url)
    {
        if (!FeedUpdateService.IsFeedUrl(url))
            return Lang.T("updates.invalid_url");
        if (configuration.UpdateFeedUrls.Any(u => string.Equals(u, url, StringComparison.OrdinalIgnoreCase)))
            return Lang.T("updates.already_added");
        return null;
    }

    // ---- File import ----

    private void OpenImportDialog()
    {
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        fileDialog.OpenFileDialog(
            Lang.T("add.file_dialog"),
            "Harmonia{.hpk,.br}",
            (ok, paths) =>
            {
                if (ok && paths.Count > 0)
                    StartStaging(paths[0]);
            },
            1,
            Directory.Exists(downloads) ? downloads : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            false);
    }

    // Verification hashes and parses the whole pack, so it runs off the UI thread.
    private void StartStaging(string path)
    {
        if (staging is not null)
            return;

        importMessage = null;
        page = Page.Translations;
        staging = Task.Run(() => installer.Stage(path));
    }

    private void PollStaging()
    {
        if (staging is not { IsCompleted: true } task)
            return;

        staging = null;
        if (!task.IsCompletedSuccessfully)
        {
            importMessage = (Lang.T("import.failed", task.Exception?.GetBaseException().Message ?? "?"), true);
            return;
        }

        var staged = task.Result;
        if (PublisherTrust.InstallsWithoutConfirmation(staged.Trust) && !staged.IsDowngrade)
        {
            Commit(staged, trustConfirmed: false);
            return;
        }

        pendingImport?.Dispose();
        pendingImport = staged;
        pendingFeedTrust = null;
        openTrustPopup = true;
    }

    private void Commit(StagedPack staged, bool trustConfirmed)
    {
        try
        {
            installer.Commit(staged, trustConfirmed);
            var packId = staged.Manifest.PackId;
            if (string.Equals(packId, info.LoadedPackId, StringComparison.Ordinal))
                session.IsRestartRequired = true;

            SelectIfNone(packId);
            var name = packs.TryGet(packId)?.DisplayName ?? packId;
            var turnsOn = string.Equals(configuration.ActivePackId, packId, StringComparison.Ordinal) &&
                !string.Equals(info.LoadedPackId, packId, StringComparison.Ordinal);
            importMessage = (turnsOn ? Lang.T("import.done_turns_on", name) : Lang.T("import.done", name), false);
        }
        catch (Exception ex)
        {
            importMessage = (Lang.T("import.failed", ex.Message), true);
        }
        finally
        {
            staged.Dispose();
        }
    }

    // ---- Feed installs ----

    // A key nobody pinned yet for this pack is shown to the player first.
    private void StartFeedInstall(FeedStatus status, string title)
    {
        if (status.TrustFingerprint is { } fingerprint)
        {
            pendingImport?.Dispose();
            pendingImport = null;
            pendingFeedTrust = new FeedTrust(status.Url, title, fingerprint);
            openTrustPopup = true;
            return;
        }

        Install(status.Url, null);
    }

    private void Install(string url, string? confirmedFingerprint)
    {
        if (!installs.ContainsKey(url))
            installs[url] = feeds.InstallUpdateAsync(url, confirmedFingerprint);
    }

    private void PollInstalls()
    {
        foreach (var (url, task) in installs.ToArray())
        {
            if (!task.IsCompleted)
                continue;

            installs.Remove(url);
            var status = FeedStatusFor(url);
            if (task.IsCompletedSuccessfully && task.Result)
            {
                if (status?.PackId is { } packId)
                    SelectIfNone(packId);
            }
            else if (status is { Status: FeedPackStatus.NeedsTrust, TrustFingerprint: not null })
            {
                StartFeedInstall(status, status.Title ?? status.PackId ?? url);
            }
        }

        foreach (var url in autoInstall.ToArray())
        {
            var status = FeedStatusFor(url);
            if (status is null || status.Status is FeedPackStatus.Unknown or FeedPackStatus.Checking)
                continue;

            autoInstall.Remove(url);
            if (status.Status is FeedPackStatus.UpdateAvailable or FeedPackStatus.NeedsTrust && packs.TryGet(status.PackId) is null)
                StartFeedInstall(status, status.Title ?? status.PackId ?? url);
        }
    }

    // ---- Publisher confirmation ----

    private void DrawTrustPopup()
    {
        var title = Lang.T("trust.title") + TrustPopupId;
        if (openTrustPopup)
        {
            ImGui.OpenPopup(title);
            openTrustPopup = false;
        }

        ImGui.SetNextWindowSize(new Vector2(500 * Ui.Scale, 0), ImGuiCond.Appearing);
        using var popup = ImRaii.PopupModal(title, ImGuiWindowFlags.NoResize);
        if (!popup)
            return;

        using var wrap = ImRaii.TextWrapPos(0);
        if (pendingImport is { } staged)
            DrawImportTrust(staged);
        else if (pendingFeedTrust is { } feed)
            DrawFeedTrust(feed);
        else
            ImGui.CloseCurrentPopup();
    }

    private void DrawImportTrust(StagedPack staged)
    {
        var name = staged.Manifest.Title;
        var fingerprint = staged.Fingerprint is null ? string.Empty : HpkSignature.ShortFingerprint(staged.Fingerprint);
        switch (staged.Trust)
        {
            case PublisherTrustState.FirstUse:
                ImGui.TextWrapped(Lang.T("trust.first_use", name));
                Ui.Gap(4);
                Ui.Hint(Lang.T("trust.compare"));
                Key(fingerprint);
                break;
            case PublisherTrustState.KeyChanged:
                configuration.PinnedPublisherKeys.TryGetValue(staged.Manifest.PackId, out var pinned);
                Ui.IconText(FontAwesomeIcon.ExclamationTriangle, Ui.Warn, Lang.T("trust.key_changed", name));
                Ui.Gap(4);
                Ui.Row(Lang.T("trust.old_key"), pinned is null ? "?" : HpkSignature.ShortFingerprint(pinned), 130);
                Ui.Row(Lang.T("trust.new_key"), fingerprint, 130);
                break;
            case PublisherTrustState.Unsigned:
                Ui.IconText(FontAwesomeIcon.ExclamationTriangle, Ui.Warn, Lang.T("trust.unsigned", name));
                break;
        }

        if (staged.IsDowngrade)
        {
            Ui.Gap(4);
            Ui.IconText(FontAwesomeIcon.History, Ui.Warn, Lang.T("trust.downgrade", staged.Manifest.Version, staged.Manifest.Sequence, staged.InstalledSequence ?? 0));
        }

        var choice = TrustButtons(Lang.T("trust.install"));
        if (choice is null)
            return;

        pendingImport = null;
        ImGui.CloseCurrentPopup();
        if (choice == true)
            Commit(staged, trustConfirmed: true);
        else
            staged.Dispose();
    }

    private void DrawFeedTrust(FeedTrust feed)
    {
        ImGui.TextWrapped(Lang.T("trust.first_use", feed.Title));
        Ui.Gap(4);
        Ui.Hint(Lang.T("trust.compare"));
        Key(HpkSignature.ShortFingerprint(feed.Fingerprint));

        var choice = TrustButtons(Lang.T("trust.trust_install"));
        if (choice is null)
            return;

        pendingFeedTrust = null;
        ImGui.CloseCurrentPopup();
        if (choice == true)
            Install(feed.Url, feed.Fingerprint);
    }

    private static void Key(string fingerprint)
    {
        Ui.Gap(2);
        using var indent = ImRaii.PushIndent();
        Ui.Icon(FontAwesomeIcon.Key, Ui.Info);
        ImGui.SameLine();
        ImGui.TextUnformatted(fingerprint);
    }

    // true: install, false: cancel, null: no choice yet.
    private static bool? TrustButtons(string installLabel)
    {
        Ui.Gap(10);
        if (Ui.PrimaryButton(FontAwesomeIcon.Check, installLabel))
            return true;
        ImGui.SameLine();
        if (ImGui.Button(Lang.T("common.cancel")))
            return false;
        return null;
    }
}
