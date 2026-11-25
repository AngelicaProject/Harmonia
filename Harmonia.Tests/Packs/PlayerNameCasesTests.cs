using System.Text;
using Harmonia.Packs.Hpk;
using Harmonia.Runtime;
using Lumina.Text;
using Lumina.Text.Expressions;
using Lumina.Text.Payloads;
using Lumina.Text.ReadOnly;
using Xunit;

namespace Harmonia.Tests.Packs;

public sealed unsafe class PlayerNameCasesTests
{
    private static ReadOnlySpan<byte> FullName => [0x02, 0x29, 0x03, 0xEB, 0x02, 0x03];

    [Fact]
    public void Only_the_value_displayed_by_a_branch_is_replaced()
    {
        var source = new SeStringBuilder()
            .BeginMacro(MacroCode.If)
            .BeginBinaryExpression((ExpressionType)0xE5)
            .AppendGlobalStringExpression(1)
            .AppendGlobalStringExpression(2)
            .EndExpression()
            .AppendStringExpression(new ReadOnlySeStringSpan(FullName))
            .AppendStringExpression("Дальше".AsSpan())
            .EndMacro()
            .ToArray();
        var offset = source.AsSpan().IndexOf(FullName);
        Assert.True(offset > 0);
        HpkNameCase[] annotation = [new((ushort)offset, PlayerNamePart.Full, PlayerNameCase.Dative)];

        var result = HpkNameCase.Format(source, annotation, (_, _) => "Алисе Грей");

        Assert.NotNull(result);
        var output = new ReadOnlySeStringSpan(result).GetEnumerator();
        Assert.True(output.MoveNext());
        var expressions = output.Current.GetEnumerator();
        Assert.True(expressions.MoveNext());
        Assert.True(expressions.Current.Body.SequenceEqual(source.AsSpan(3, 5)));
        Assert.True(expressions.MoveNext());
        Assert.True(expressions.Current.TryGetString(out var form));
        Assert.True(form.Data.SequenceEqual("Алисе Грей"u8));
        Assert.True(expressions.MoveNext());
        Assert.True(expressions.Current.TryGetString(out var other));
        Assert.True(other.Data.SequenceEqual("Дальше"u8));
        Assert.Null(HpkNameCase.Format(source, annotation, (_, _) => null));
        Assert.Null(HpkNameCase.Format(source, annotation, (_, _) => "Алиса\u0002"));
    }

    [Fact]
    public void Aeria_project_prima_pilot_loads_and_formats_all_five_replies()
    {
        if (Environment.GetEnvironmentVariable("HARMONIA_NAME_CASES_PILOT") is not { Length: > 0 } path)
            return;

        using var pack = HpkFile.Open(path, HpkOpenMode.Full);
        Assert.True(pack.HasNameCases);
        Assert.Equal(5, pack.CellCount);
        var substitutions = 0;
        uint[] pilotRows = [590971, 33, 93, 51, 111];
        for (var sheet = 0; sheet < pilotRows.Length; sheet++)
        {
            Assert.True(pack.TryGetRow(sheet, pilotRows[sheet], 0, out var row));
            for (var i = 0; i < row.CellCount; i++)
            {
                var cell = pack.GetCell(row, i, out _);
                var annotations = pack.GetNameCases(row, i);
                substitutions += annotations.Length;
                var formatted = HpkNameCase.Format(new ReadOnlySpan<byte>(cell.String, cell.Length), annotations,
                    (part, nameCase) => (part, nameCase) switch
                    {
                        (PlayerNamePart.First, PlayerNameCase.Nominative) => "Алиса",
                        (PlayerNamePart.First, PlayerNameCase.Dative) => "Алисе",
                        (PlayerNamePart.Full, PlayerNameCase.Nominative) => "Алиса Грей",
                        (PlayerNamePart.Full, PlayerNameCase.Instrumental) => "Алисой Грей",
                        (PlayerNamePart.Family, PlayerNameCase.Accusative) => "Грей",
                        _ => null,
                    });
                Assert.NotNull(formatted);
                Assert.Contains("Алис", Encoding.UTF8.GetString(formatted));
            }
        }
        Assert.Equal(6, substitutions);
    }

    [Fact]
    public void Dialogue_names_are_selected_using_the_current_character()
    {
        HpkNameCase[] cases = [new(0, PlayerNamePart.Full, PlayerNameCase.Nominative)];
        var result = DialogueNames.Format(FullName, cases, "Alexandra Rostova", (_, _) => "Александра Ростова");
        Assert.NotNull(result);
        AssertCharacterBranches(result, FullName, "Александра Ростова");
    }

    [Fact]
    public void Suggested_forms_cover_all_cases_and_preserve_manual_edits()
    {
        var forms = new Dictionary<PlayerNameCase, DialogueNameForms>
        {
            [PlayerNameCase.Nominative] = new() { First = "Александра", Family = "Ростова" },
        };
        DialogueNameSuggestions.FillEmpty(forms);
        Assert.Equal(
            ["Александра Ростова", "Александры Ростовой", "Александре Ростовой",
             "Александру Ростову", "Александрой Ростовой", "Александре Ростовой"],
            Enum.GetValues<PlayerNameCase>().Select(nameCase => forms[nameCase].Full));

        forms[PlayerNameCase.Instrumental].First = "Александрою";
        forms[PlayerNameCase.Instrumental].Full = string.Empty;
        DialogueNameSuggestions.FillEmpty(forms);
        Assert.Equal("Александрою Ростовой", forms[PlayerNameCase.Instrumental].Full);
        Assert.Equal("Александрою", forms[PlayerNameCase.Instrumental].First);
    }

    [Fact]
    public void One_name_field_fills_all_cases_and_refreshes_only_generated_forms()
    {
        var forms = new Dictionary<PlayerNameCase, DialogueNameForms>();
        DialogueNameSuggestions.ApplyName(forms, "  Александра   Ростова ");
        Assert.Equal(6, forms.Count);
        Assert.Equal("Александре Ростовой", forms[PlayerNameCase.Dative].Full);
        forms[PlayerNameCase.Instrumental].First = "Александрою";

        DialogueNameSuggestions.ApplyName(forms, "Александра Орлова");
        Assert.Equal("Александра Орлова", forms[PlayerNameCase.Nominative].Full);
        Assert.Equal("Александре Орловой", forms[PlayerNameCase.Dative].Full);
        Assert.Equal("Александрою Орловой", forms[PlayerNameCase.Instrumental].Full);
    }

    [Fact]
    public void Installed_project_prima_accepts_the_baderon_and_coach_annotations()
    {
        var installed = Environment.GetEnvironmentVariable("HARMONIA_DIALOGUE_TEST_PACK");
        var sidecar = Environment.GetEnvironmentVariable("HARMONIA_DIALOGUE_TEST_DIR");
        if (string.IsNullOrEmpty(installed) || string.IsNullOrEmpty(sidecar))
            return;

        using var pack = HpkFile.Open(installed, HpkOpenMode.Full);
        using var runtime = new TranslationRuntime(pack, "test", installed);
        runtime.SetDialogueNames(new DialogueNameSettings
        {
            OriginalName = "Alexandra Rostova",
            Forms = new() { [PlayerNameCase.Nominative] = new() { First = "Александра", Full = "Александра Ростова" } },
        }, sidecar, new NullLog());

        (string Sheet, uint Row, string Expected)[] replies =
        [
            ("custom/001/RegSeaTheDrowningWench_00158", 22, "Александра, ну и рад"),
            ("custom/003/CmnDefBeginnerGuide_00327", 27, "Да вы же Александра Ростова!"),
        ];
        foreach (var (name, rowId, expected) in replies)
        {
            Assert.True(pack.TryGetSheet(name, out var sheet));
            var layout = pack.GetLayout(sheet).Select(c => new StringColumn((ushort)c.ColumnIndex, (ushort)c.Offset)).ToArray();
            Assert.Equal(sheet, runtime.BindSheet(name, false, layout));
            Assert.True(runtime.TryGetRow(sheet, rowId, 0, out var row));
            var index = Enumerable.Range(0, row.CellCount).Single(i =>
            {
                pack.GetCell(row, i, out var ordinal);
                return layout[ordinal].Index == 1;
            });
            var cell = runtime.GetCell(row, index, out var ordinal);
            var result = runtime.GetReplacement(sheet, rowId, 0, row, index, ordinal, cell);
            AssertCharacterBranches(new ReadOnlySpan<byte>(result.Text, result.Length),
                new ReadOnlySpan<byte>(cell.String, cell.Length), expected);
        }
    }

    private static void AssertCharacterBranches(ReadOnlySpan<byte> result, ReadOnlySpan<byte> original, string expected)
    {
        var payloads = new ReadOnlySeStringSpan(result).GetEnumerator();
        Assert.True(payloads.MoveNext());
        Assert.Equal(MacroCode.If, payloads.Current.MacroCode);
        Assert.True(payloads.Current.TryGetExpression(out var condition, out var localized, out var fallback));
        Assert.True(condition.TryGetBinaryExpression(out var comparison, out var left, out var right));
        Assert.Equal((byte)ExpressionType.Equal, comparison);
        Assert.True(left.Body.SequenceEqual(new byte[] { 0xEB, 0x02 }));
        Assert.True(right.TryGetString(out var name));
        Assert.True(name.Data.SequenceEqual("Alexandra Rostova"u8));
        Assert.True(localized.TryGetString(out var translated));
        Assert.Contains(expected, Encoding.UTF8.GetString(translated.Data));
        Assert.True(fallback.TryGetString(out var baseText));
        Assert.True(baseText.Data.SequenceEqual(original));
        Assert.False(payloads.MoveNext());
    }
}
