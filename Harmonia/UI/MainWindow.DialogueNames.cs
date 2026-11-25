using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Harmonia.Localization;
using Harmonia.Packs.Hpk;
using Harmonia.Runtime;

namespace Harmonia.UI;

internal sealed partial class MainWindow
{
    private static readonly PlayerNameCase[] DialogueCases = Enum.GetValues<PlayerNameCase>();
    private readonly Func<string?> currentCharacterName;
    private DialogueNameSettings dialogueNameDraft = new();
    private string dialogueNameInput = string.Empty;
    private string appliedDialogueName = string.Empty;

    private void DrawDialogueNameSettings()
    {
        bool open;
        using (Ui.BeginCard("dialogue_name"))
        {
            ImGui.TextUnformatted(Lang.T("settings.dialogue_name"));
            Ui.Hint(Lang.T("settings.dialogue_name_hint"));
            open = Ui.Button(FontAwesomeIcon.Edit, Lang.T("settings.dialogue_name_edit"));
        }
        var title = Lang.T("settings.dialogue_name") + "###harmonia_dialogue_cases";
        if (open)
        {
            dialogueNameDraft = CopyDialogueNameSettings(configuration.DialogueName, currentCharacterName());
            var nominative = dialogueNameDraft.Forms[PlayerNameCase.Nominative];
            dialogueNameInput = appliedDialogueName = string.IsNullOrWhiteSpace(nominative.Full)
                ? $"{nominative.First} {nominative.Family}".Trim()
                : nominative.Full;
            ImGui.OpenPopup(title);
        }

        DrawDialogueNameEditor(title);
    }

    private void DrawDialogueNameEditor(string title)
    {
        var width = Math.Min(1000 * Ui.Scale, ImGui.GetIO().DisplaySize.X - 30 * Ui.Scale);
        ImGui.SetNextWindowSize(new Vector2(width, 0), ImGuiCond.Appearing);
        using var popup = ImRaii.PopupModal(title, ImGuiWindowFlags.NoResize);
        if (!popup)
            return;

        if (string.IsNullOrWhiteSpace(dialogueNameDraft.OriginalName))
        {
            ImGui.TextWrapped(Lang.T("settings.dialogue_name_login"));
            if (ImGui.Button(Lang.T("common.cancel")))
                ImGui.CloseCurrentPopup();
            return;
        }

        var fillLabel = Lang.T("settings.dialogue_name_fill");
        var fillWidth = Ui.ButtonWidth(FontAwesomeIcon.Magic, fillLabel);
        ImGui.SetNextItemWidth(Math.Max(100 * Ui.Scale,
            ImGui.GetContentRegionAvail().X - fillWidth - ImGui.GetStyle().ItemSpacing.X));
        var fill = ImGui.InputTextWithHint("##dialogue_name_input", Lang.T("settings.dialogue_name_input"),
            ref dialogueNameInput, 128, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        using (ImRaii.Disabled(string.IsNullOrWhiteSpace(dialogueNameInput)))
        {
            fill |= Ui.Button(FontAwesomeIcon.Magic, fillLabel);
        }
        if (fill && !string.IsNullOrWhiteSpace(dialogueNameInput))
            ApplyDialogueNameInput();

        Ui.Gap(4);
        DrawDialogueNameTable();
        Ui.Gap(6);

        var saveLabel = Lang.T("settings.dialogue_name_save");
        var cancelLabel = Lang.T("common.cancel");
        var buttonX = ImGui.GetCursorPosX();
        var actionsWidth = Ui.ButtonWidth(FontAwesomeIcon.Check, saveLabel) +
            ImGui.CalcTextSize(cancelLabel).X + 2 * ImGui.GetStyle().FramePadding.X + ImGui.GetStyle().ItemSpacing.X;
        ImGui.SetCursorPosX(Math.Max(buttonX, buttonX + ImGui.GetContentRegionAvail().X - actionsWidth));
        if (ImGui.Button(cancelLabel))
            ImGui.CloseCurrentPopup();
        ImGui.SameLine();
        if (Ui.PrimaryButton(FontAwesomeIcon.Check, saveLabel))
        {
            if (dialogueNameInput != appliedDialogueName && !string.IsNullOrWhiteSpace(dialogueNameInput))
                ApplyDialogueNameInput();
            configuration.DialogueName = dialogueNameDraft;
            save();
            ImGui.CloseCurrentPopup();
        }
    }

    private void ApplyDialogueNameInput()
    {
        DialogueNameSuggestions.ApplyName(dialogueNameDraft.Forms, dialogueNameInput);
        appliedDialogueName = dialogueNameInput;
    }

    private void DrawDialogueNameTable()
    {
        using var table = ImRaii.Table("##dialogue_cases", 5,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp);
        if (!table)
            return;

        var labelWidth = DialogueCases.Max(nameCase => ImGui.CalcTextSize(DialogueCaseLabel(nameCase)).X);
        ImGui.TableSetupColumn(Lang.T("settings.dialogue_name_case"), ImGuiTableColumnFlags.WidthFixed, labelWidth + 8 * Ui.Scale);
        ImGui.TableSetupColumn(Lang.T("settings.dialogue_name_first"), ImGuiTableColumnFlags.WidthStretch, 1);
        ImGui.TableSetupColumn(Lang.T("settings.dialogue_name_family"), ImGuiTableColumnFlags.WidthStretch, 1);
        ImGui.TableSetupColumn(Lang.T("settings.dialogue_name_full"), ImGuiTableColumnFlags.WidthStretch, 1.65f);
        ImGui.TableSetupColumn(Lang.T("settings.dialogue_name_example"), ImGuiTableColumnFlags.WidthStretch, 2.7f);
        ImGui.TableHeadersRow();

        foreach (var nameCase in DialogueCases)
        {
            using var id = ImRaii.PushId((int)nameCase);
            var form = dialogueNameDraft.Forms[nameCase];
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(DialogueCaseLabel(nameCase));
            ImGui.TableNextColumn();
            form.First = EditDialogueName("first", form.First);
            ImGui.TableNextColumn();
            form.Family = EditDialogueName("family", form.Family);
            ImGui.TableNextColumn();
            form.Full = EditDialogueName("full", form.Full);
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            using var wrap = ImRaii.TextWrapPos(0);
            ImGui.TextUnformatted(DialogueCaseExample(nameCase, form));
        }
    }

    private static string EditDialogueName(string id, string? value)
    {
        value ??= string.Empty;
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##" + id, ref value, 128);
        return value;
    }

    private static DialogueNameSettings CopyDialogueNameSettings(DialogueNameSettings? saved, string? currentName)
    {
        var originalName = string.IsNullOrWhiteSpace(currentName) ? saved?.OriginalName ?? string.Empty : currentName;
        if (!string.Equals(saved?.OriginalName, originalName, StringComparison.Ordinal))
            saved = null;
        var draft = new DialogueNameSettings { OriginalName = originalName };
        foreach (var nameCase in DialogueCases)
        {
            var source = saved?.Forms?.GetValueOrDefault(nameCase);
            draft.Forms[nameCase] = source is null
                ? new DialogueNameForms()
                : new DialogueNameForms { First = source.First, Family = source.Family, Full = source.Full };
        }
        if (saved?.Forms?.Count == 1 && saved.Forms.ContainsKey(PlayerNameCase.Nominative))
            DialogueNameSuggestions.FillEmpty(draft.Forms);
        return draft;
    }

    private static string DialogueCaseLabel(PlayerNameCase nameCase) => nameCase switch
    {
        PlayerNameCase.Genitive => Lang.T("settings.dialogue_name_genitive"),
        PlayerNameCase.Dative => Lang.T("settings.dialogue_name_dative"),
        PlayerNameCase.Accusative => Lang.T("settings.dialogue_name_accusative"),
        PlayerNameCase.Instrumental => Lang.T("settings.dialogue_name_instrumental"),
        PlayerNameCase.Prepositional => Lang.T("settings.dialogue_name_prepositional"),
        _ => Lang.T("settings.dialogue_name_nominative"),
    };

    private static string DialogueCaseExample(PlayerNameCase nameCase, DialogueNameForms form)
    {
        var name = string.IsNullOrWhiteSpace(form.Full) ? $"{form.First} {form.Family}".Trim() : form.Full;
        if (name.Length == 0)
            name = "[...]";

        return nameCase switch
        {
            PlayerNameCase.Genitive => Lang.T("settings.dialogue_example_genitive", name),
            PlayerNameCase.Dative => Lang.T("settings.dialogue_example_dative", name),
            PlayerNameCase.Accusative => Lang.T("settings.dialogue_example_accusative", name),
            PlayerNameCase.Instrumental => Lang.T("settings.dialogue_example_instrumental", name),
            PlayerNameCase.Prepositional => Lang.T("settings.dialogue_example_prepositional", name),
            _ => Lang.T("settings.dialogue_example_nominative", name),
        };
    }
}
