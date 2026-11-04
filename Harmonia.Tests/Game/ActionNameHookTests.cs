using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using Harmonia.Runtime;
using Xunit;
using Xunit.Abstractions;

namespace Harmonia.Tests.Game;

// HARMONIA_GAME=<game>/game/sqpack dotnet test --filter ActionNameHook
public sealed class ActionNameHookTests(ITestOutputHelper output)
{
    [Fact]
    public void Installed_macro_icon_search_reaches_the_sheet_matcher_with_the_expected_entry_layout()
    {
        var path = Environment.GetEnvironmentVariable("HARMONIA_GAME");
        if (string.IsNullOrEmpty(path))
            return;
        using var stream = File.OpenRead(Path.Combine(path, "..", "ffxiv_dx11.exe"));
        using var pe = new PEReader(stream);
        var image = new byte[pe.PEHeaders.PEHeader!.SizeOfImage];
        foreach (var section in pe.PEHeaders.SectionHeaders)
            pe.GetSectionData(section.VirtualAddress).GetContent().AsSpan().CopyTo(image.AsSpan(section.VirtualAddress));
        var text = pe.PEHeaders.SectionHeaders.Single(static s => s.Name == ".text");
        var textStart = text.VirtualAddress;
        var textEnd = textStart + text.VirtualSize;

        int Find(string signature)
        {
            var pattern = signature.Split(' ').Select(static s => s == "??" ? (byte?)null : Convert.ToByte(s, 16)).ToArray();
            var found = new List<int>();
            for (var i = textStart; i + pattern.Length <= textEnd; i++)
            {
                var matches = true;
                for (var j = 0; j < pattern.Length && matches; j++)
                    matches = pattern[j] is not { } b || image[i + j] == b;
                if (matches)
                    found.Add(i);
            }
            return Assert.Single(found);
        }

        int Target(int call) => call + 5 + BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(call + 1));

        List<int> CallsTo(int target)
        {
            var calls = new List<int>();
            for (var i = textStart; i + 5 <= textEnd; i++)
            {
                if (image[i] == 0xE8 && Target(i) == target)
                    calls.Add(i);
            }
            return calls;
        }

        var matcher = Find(ActionNameAliases.MatchSignature);
        // The matcher reads the native sheet pointer, name column and row
        // bounds from the same descriptor whose Utf8String the hook checks.
        var matcherCode = image.AsSpan(matcher, 0x500);
        Assert.True(matcherCode.IndexOf(new byte[] { 0x48, 0x8B, 0x4F, 0x68 }) >= 0);
        Assert.True(matcherCode.IndexOf(new byte[] { 0x0F, 0xBF, 0x6F, 0x70 }) >= 0);
        Assert.True(matcherCode.IndexOf(new byte[] { 0x0F, 0xBF, 0x57, 0x74 }) >= 0);

        var search = Find("48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 41 54 41 55 41 56 41 57 48 83 EC ?? 41 8B C0 4C 8B E2");
        Assert.Contains(CallsTo(matcher), call => call >= search && call < search + 0x100);
        // RaptureShellModule forwards all five search arguments on the
        // nonnegative category branch; the tail jump preserves stack args.
        var wrapper = Find("48 83 EC 38 4C 8B D1 45 85 C0 78 ?? 48 83 C4 38 E9");
        Assert.Equal(search, Target(wrapper + 16));

        var macroParseCall = Find("E8 ?? ?? ?? ?? 48 BA ?? ?? ?? ?? ?? ?? ?? ?? 84 C0");
        var directory = pe.PEHeaders.PEHeader.ExceptionTableDirectory;
        var macroStart = -1;
        var macroEnd = -1;
        for (var i = directory.RelativeVirtualAddress; i < directory.RelativeVirtualAddress + directory.Size; i += 12)
        {
            var begin = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(i));
            var end = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(i + 4));
            if (begin <= macroParseCall && macroParseCall < end)
                (macroStart, macroEnd) = (begin, end);
        }
        Assert.True(macroStart >= 0);
        Assert.Contains(CallsTo(wrapper), call => call >= macroStart && call < macroEnd);
        output.WriteLine($"Macro icon resolver +{macroStart:X}; search wrapper +{wrapper:X}; sheet search +{search:X}; matcher +{matcher:X}.");
    }
}
