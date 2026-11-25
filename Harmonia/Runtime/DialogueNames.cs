using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Harmonia.Packs.Hpk;
using Lumina.Text;
using Lumina.Text.Expressions;
using Lumina.Text.Payloads;
using Lumina.Text.ReadOnly;

namespace Harmonia.Runtime;

internal sealed unsafe class DialogueNames : IDisposable
{
    public const string AnnotationFile = "dialogue-names.hpk";

    private readonly HpkFile pack;
    private readonly HpkFile? annotations;
    private readonly int[] annotationSheets;
    private readonly string originalName;
    private readonly Dictionary<(PlayerNamePart, PlayerNameCase), string> forms = [];
    private readonly ConcurrentDictionary<long, byte[]> prepared = new();

    public DialogueNames(HpkFile pack, HpkFile? annotations, DialogueNameSettings settings)
    {
        this.pack = pack;
        this.annotations = annotations;
        originalName = settings.OriginalName;
        foreach (var (nameCase, form) in settings.Forms ?? [])
        {
            if (form is null)
                continue;
            forms[(PlayerNamePart.Full, nameCase)] = form.Full;
            forms[(PlayerNamePart.First, nameCase)] = form.First;
            forms[(PlayerNamePart.Family, nameCase)] = form.Family;
        }

        annotationSheets = new int[pack.SheetCount];
        Array.Fill(annotationSheets, -1);
        if (annotations is null)
            return;
        for (var i = 0; i < pack.SheetCount; i++)
        {
            if (annotations.TryGetSheet(pack.SheetNames[i], out var sheet) &&
                pack.IsSubrowSheet(i) == annotations.IsSubrowSheet(sheet) &&
                pack.GetLayout(i).AsSpan().SequenceEqual(annotations.GetLayout(sheet)))
                annotationSheets[i] = sheet;
        }
    }

    public static bool IsConfigured(DialogueNameSettings? settings) =>
        settings is { OriginalName.Length: > 0 and <= 64, Forms.Count: > 0 } &&
        !string.IsNullOrWhiteSpace(settings.OriginalName) && !settings.OriginalName.Any(char.IsControl);

    public Replacement Resolve(int sheet, uint rowId, ushort subrow, HpkRow row, int index, ushort ordinal, HpkCell cell)
    {
        var fallback = new Replacement(cell.String, cell.Length);
        var name = pack.SheetNames[sheet];
        if (name != "DefaultTalk" && !name.StartsWith("quest/", StringComparison.Ordinal) &&
            !name.StartsWith("custom/", StringComparison.Ordinal) && !name.StartsWith("cut_scene/", StringComparison.Ordinal))
            return fallback;

        var key = row.FirstCell + index;
        if (prepared.TryGetValue(key, out var found))
            return AsReplacement(found);

        var cases = pack.GetNameCases(row, index);
        var text = new ReadOnlySpan<byte>(cell.String, cell.Length);
        if (cases.Length == 0 && annotations is not null && annotationSheets[sheet] is var otherSheet && otherSheet >= 0 &&
            annotations.TryGetRow(otherSheet, rowId, subrow, out var otherRow))
        {
            for (var i = 0; i < otherRow.CellCount; i++)
            {
                var candidate = annotations.GetCell(otherRow, i, out var candidateOrdinal);
                if (candidateOrdinal == ordinal && candidate.SourceGuard == cell.SourceGuard &&
                    text.SequenceEqual(new ReadOnlySpan<byte>(candidate.String, candidate.Length)))
                {
                    cases = annotations.GetNameCases(otherRow, i);
                    break;
                }
            }
        }
        if (cases.Length == 0)
            return fallback;

        var result = Format(text, cases, originalName, (part, nameCase) => forms.GetValueOrDefault((part, nameCase)));
        if (result is null)
            return fallback;

        var pinned = GC.AllocateUninitializedArray<byte>(result.Length, pinned: true);
        result.CopyTo(pinned, 0);
        return AsReplacement(prepared.GetOrAdd(key, pinned));
    }

    private static Replacement AsReplacement(byte[] text) =>
        new((byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(text)), text.Length);

    internal static byte[]? Format(ReadOnlySpan<byte> text, ReadOnlySpan<HpkNameCase> cases, string originalName,
        Func<PlayerNamePart, PlayerNameCase, string?> forms)
    {
        if (HpkNameCase.Format(text, cases, forms) is not { } localized)
            return null;

        // The game may keep this row across logins. It chooses the branch
        // with the current character name each time it displays the dialogue.
        var conditional = new SeStringBuilder()
            .BeginMacro(MacroCode.If)
            .BeginBinaryExpression(ExpressionType.Equal)
            .AppendGlobalStringExpression(1)
            .AppendStringExpression(originalName.AsSpan())
            .EndExpression()
            .AppendStringExpression(new ReadOnlySeStringSpan(localized))
            .AppendStringExpression(new ReadOnlySeStringSpan(text))
            .EndMacro()
            .ToArray();
        return conditional.Length <= HpkFormat.MaxStringLength && SeStringCheck.IsWellFormed(conditional)
            ? conditional : null;
    }

    public void Dispose() => annotations?.Dispose();
}
