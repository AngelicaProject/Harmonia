using System.Globalization;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Harmonia.Compatibility;
using Harmonia.Fonts;
using Harmonia.Game;
using Harmonia.Localization;
using Harmonia.Runtime;

namespace Harmonia.UI;

// Everything a publisher or a Harmonia developer needs to understand a
// problem report, kept away from the Translations page.
internal sealed partial class MainWindow
{
    private const float DiagnosticsLabelWidth = 240;

    private const string ReportFileName = "harmonia-report.txt";

    private DateTime copiedAt;
    private string? reportPath;
    private string? reportError;

    private void SaveReport()
    {
        try
        {
            var path = Path.Combine(packs.ResourcesDir, ReportFileName);
            File.WriteAllText(path, DiagnosticsText());
            reportPath = path;
            reportError = null;
        }
        catch (Exception ex)
        {
            reportPath = null;
            reportError = ex.Message;
        }
    }

    private void DrawDiagnostics()
    {
        Ui.Heading(headingFont, Lang.T("nav.diagnostics"));
        Ui.Hint(Lang.T("info.intro"));
        Ui.Gap(4);
        var copied = DateTime.UtcNow - copiedAt < TimeSpan.FromSeconds(2);
        if (Ui.PrimaryButton(copied ? FontAwesomeIcon.Check : FontAwesomeIcon.Copy, copied ? Lang.T("info.copied") : Lang.T("info.copy")))
        {
            ImGui.SetClipboardText(DiagnosticsText());
            copiedAt = DateTime.UtcNow;
        }

        // The game's clipboard is not always shared with the rest of the
        // system, so the report can also be saved as a file.
        ImGui.SameLine();
        if (Ui.Button(FontAwesomeIcon.Save, Lang.T("info.save")))
            SaveReport();

        if (reportPath is not null)
        {
            Ui.Gap(2);
            Ui.Hint(Lang.T("info.saved", reportPath));
            if (Ui.LinkButton(FontAwesomeIcon.FolderOpen, Lang.T("details.open_folder")))
                Ui.OpenFolder(Path.GetDirectoryName(reportPath)!);
        }
        else if (reportError is not null)
        {
            Ui.Gap(2);
            Ui.Hint(Lang.T("info.save_failed", reportError));
        }

        Ui.Gap(8);

        using (Ui.BeginCard("environment"))
        {
            Row(Lang.T("info.plugin_version"), info.PluginVersion);
            Row(Lang.T("info.platform"), Platform.Describe());
            Row(Lang.T("info.game_version"), packs.CurrentGameVersion ?? Lang.T("common.unknown"));
            Row(Lang.T("info.client_language"), packs.ClientLanguage is { } language
                ? $"{Ui.LanguageName(language)} ({language})"
                : Lang.T("common.unknown"));
        }

        using (Ui.BeginCard("engine", HasProblem ? Ui.Bad : null))
        {
            Row(Lang.T("info.pack"), info.Runtime is { } loaded ? $"{loaded.Info.Title} ({loaded.Info.PackId})" : Lang.T("info.none"));
            Row(Lang.T("info.engine"), EngineState());
            DrawFontState();
        }

        if (info.Runtime is not { } runtime)
            return;

        var totals = runtime.GetTotals();
        using (Ui.BeginCard("session"))
        {
            ImGui.TextDisabled(Lang.T("info.section_session"));
            Ui.Gap(2);
            Row(Lang.T("info.applied"), totals.Applied.ToString("N0", CultureInfo.CurrentCulture));
            Row(Lang.T("info.source_changed"), totals.SourceChanged.ToString("N0", CultureInfo.CurrentCulture));
            if (runtime.UntranslatedSheets > 0)
                Row(Lang.T("info.untranslated_sheets"), runtime.UntranslatedSheets.ToString("N0", CultureInfo.CurrentCulture));
            if (info.CompatibilityAtStart.Count > 0)
            {
                Row(Lang.T("info.compatibility"), string.Join(", ", info.CompatibilityAtStart));
                Row(Lang.T("info.rows_kept"), totals.Kept.ToString("N0", CultureInfo.CurrentCulture));
            }
            if (totals.MatchRate is { } rate)
                Row(Lang.T("info.match_rate"), rate.ToString("P1", CultureInfo.CurrentCulture));
            if (totals.LayoutMismatchSheets > 0)
                Ui.IconText(FontAwesomeIcon.ExclamationTriangle, Ui.Warn, Lang.T("info.layout_mismatch", totals.LayoutMismatchSheets));
            Ui.Gap(2);
            Ui.Hint(Lang.T("info.counts_hint"));
        }

        if (ImGui.CollapsingHeader(Lang.T("diagnostics.sheets")))
            DrawSheetTable(runtime);
    }

    private static void Row(string label, string value) => Ui.Row(label, value, DiagnosticsLabelWidth);

    private void DrawFontState()
    {
        if (info.Fonts is not { State: not GameFontsState.NotInPack } fonts)
            return;

        var applied = fonts.Reports.Where(static r => r.Status == FontTargetStatus.Applied).Select(static r => r.Target).Distinct().Count();
        Row(Lang.T("info.fonts"), fonts.State switch
        {
            GameFontsState.Applied => Lang.T("info.fonts_applied", applied),
            GameFontsState.NeedsReload => Lang.T("info.fonts_needs_reload"),
            GameFontsState.PenumbraMissing => Lang.T("info.fonts_no_penumbra"),
            _ => Lang.T("info.fonts_failed", fonts.Error ?? "?"),
        });

        // The title screen has no copy of some sizes (AXIS_96), so a missing
        // lobby table is expected.
        var skipped = fonts.Reports.Where(static r => r.Status is FontTargetStatus.MetricsChanged or FontTargetStatus.NoRoom ||
            (r.Status == FontTargetStatus.MissingFont && r.Set != FontSet.Lobby.Name)).ToList();
        if (skipped.Count > 0)
            Ui.IconText(FontAwesomeIcon.ExclamationTriangle, Ui.Warn, Lang.T("info.fonts_skipped", string.Join(", ", skipped.Select(static r => $"{r.Target} ({r.Set})"))));
        if (fonts.Error is { } error && fonts.State != GameFontsState.Failed)
            Ui.Hint(error);
    }

    private string EngineState()
    {
        if (info.Reloaded)
            return Lang.T("info.engine_reloaded");
        if (info.PackError is { } packError)
            return Lang.T("info.pack_failed", packError);
        if (info.Runtime is null)
            return Lang.T("info.engine_idle");
        if (info.Hooks is null)
            return Lang.T("info.engine_failed", info.HookError ?? "?");
        return info.Hooks.Errors == 0
            ? Lang.T("info.engine_running")
            : Lang.T("info.engine_errors", info.Hooks.Errors);
    }

    private static void DrawSheetTable(TranslationRuntime runtime)
    {
        var sheets = runtime.GetSheetStats();
        if (sheets.Count == 0)
        {
            Ui.Hint(Lang.T("diagnostics.no_sheets"));
            return;
        }

        using var table = ImRaii.Table("##sheets", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp);
        if (!table)
            return;

        ImGui.TableSetupColumn(Lang.T("diagnostics.sheet"));
        ImGui.TableSetupColumn(Lang.T("diagnostics.applied"));
        ImGui.TableSetupColumn(Lang.T("diagnostics.source_changed"));
        ImGui.TableSetupColumn(Lang.T("diagnostics.rows"));
        ImGui.TableSetupColumn(Lang.T("diagnostics.problems"));
        ImGui.TableHeadersRow();
        foreach (var sheet in sheets)
        {
            ImGui.TableNextRow();
            Cell(sheet.SheetName);
            Cell(sheet.Applied.ToString(CultureInfo.InvariantCulture));
            Cell(sheet.SourceChanged.ToString(CultureInfo.InvariantCulture));
            Cell(sheet.RowsRebuilt.ToString(CultureInfo.InvariantCulture));
            Cell(sheet.LayoutMismatch
                ? Lang.T("diagnostics.layout_mismatch")
                : sheet.RowsUnexpected > 0 ? Lang.T("diagnostics.unexpected_rows", sheet.RowsUnexpected) : string.Empty);
        }
    }

    private static void Cell(string text)
    {
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(text);
    }

    private string DiagnosticsText()
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Harmonia {info.PluginVersion}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Platform: {Platform.Describe()}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Game version: {packs.CurrentGameVersion ?? "?"}, client language: {packs.ClientLanguage ?? "?"}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Selected pack: {configuration.ActivePackId}, at start: {info.SelectedAtStart}, loaded: {info.LoadedPackId ?? "-"}{(info.Reloaded ? ", reloaded" : string.Empty)}");
        if (info.PackError is not null)
            text.AppendLine(CultureInfo.InvariantCulture, $"Pack error: {info.PackError}");
        if (info.HookError is not null)
            text.AppendLine(CultureInfo.InvariantCulture, $"Hook error: {info.HookError}");
        if (info.Fonts is { } fonts)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"Fonts: {fonts.State}{(fonts.Error is null ? string.Empty : ", " + fonts.Error)}");
            foreach (var report in fonts.Reports)
                text.AppendLine(CultureInfo.InvariantCulture, $"  {report.Set} {report.Target}: {report.Status} {report.Glyphs}{(report.Replaced ? " replaced" : string.Empty)}");
        }

        if (info.Hooks is { } hooks)
            text.AppendLine(CultureInfo.InvariantCulture, $"StoreRow hooks: {hooks.HashTableAddress:X}, {hooks.RingBufferAddress:X}; errors {hooks.Errors}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Letter case hooks: {(info.CaseHooks is { } caseHooks ? "on, errors " + caseHooks.Errors : "off")}");

        if (info.Runtime is { } runtime)
        {
            var t = runtime.GetTotals();
            text.AppendLine(CultureInfo.InvariantCulture, $"Pack: {runtime.Info.PackId} ({runtime.Info.Sheets} sheets, {runtime.Info.Cells} cells, {runtime.UntranslatedSheets} sheets kept untranslated)");
            text.AppendLine(CultureInfo.InvariantCulture, $"Applied {t.Applied}, source changed {t.SourceChanged}, rows {t.RowsRebuilt}, unexpected rows {t.RowsUnexpected}, layout mismatch sheets {t.LayoutMismatchSheets}");
            text.AppendLine(CultureInfo.InvariantCulture, $"Plugin compatibility: {(info.CompatibilityAtStart.Count == 0 ? "none" : string.Join(", ", info.CompatibilityAtStart))}; lines kept {t.Kept}");
            if (info.CompatibilityError is not null)
                text.AppendLine(CultureInfo.InvariantCulture, $"Plugin compatibility error: {info.CompatibilityError}");
            foreach (var error in CompatibilityProfile.LoadErrors)
                text.AppendLine(CultureInfo.InvariantCulture, $"Compatibility profile skipped: {error}");
            foreach (var s in runtime.GetSheetStats())
                text.AppendLine(CultureInfo.InvariantCulture, $"  {s.SheetName}: applied {s.Applied}, changed {s.SourceChanged}, rows {s.RowsRebuilt}, unexpected {s.RowsUnexpected}, layout mismatch {s.LayoutMismatch}");
        }

        foreach (var feed in feedState.Feeds)
            text.AppendLine(CultureInfo.InvariantCulture, $"Feed {feed.Url}: {feed.PackId ?? "-"} {feed.Status}, installed {feed.InstalledVersion ?? "-"}, available {feed.RemoteVersion ?? "-"}{(feed.Error is null ? string.Empty : ", " + feed.Error)}");

        return text.ToString();
    }
}
