using System.Buffers.Binary;
using System.Text;
using Harmonia.Dictionary;
using Harmonia.Packs.Hpk;
using Harmonia.Runtime;
using Lumina;
using Lumina.Data;
using Lumina.Excel.Sheets;
using Xunit;
using Xunit.Abstractions;

namespace Harmonia.Tests.Game;

// HARMONIA_GAME=<game>/game/sqpack dotnet test --filter OutgoingChat
public sealed class OutgoingChatTests(ITestOutputHelper output)
{
    [Fact]
    public void Installed_encoder_has_the_dispatch_and_wire_templates_we_understand()
    {
        var path = Environment.GetEnvironmentVariable("HARMONIA_GAME");
        if (string.IsNullOrEmpty(path))
            return;
        var file = File.ReadAllBytes(Path.Combine(path, "..", "ffxiv_dx11.exe"));
        var pe = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(0x3C));
        var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(pe + 6));
        var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(pe + 20));
        var imageBase = BinaryPrimitives.ReadInt64LittleEndian(file.AsSpan(pe + 24 + 24));
        var imageSize = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(pe + 24 + 56));
        var image = new byte[imageSize];
        var textStart = 0;
        var textEnd = 0;
        for (var i = 0; i < sectionCount; i++)
        {
            var section = file.AsSpan(pe + 24 + optionalSize + i * 40, 40);
            var rva = BinaryPrimitives.ReadInt32LittleEndian(section[12..]);
            var size = BinaryPrimitives.ReadInt32LittleEndian(section[16..]);
            var offset = BinaryPrimitives.ReadInt32LittleEndian(section[20..]);
            file.AsSpan(offset, size).CopyTo(image.AsSpan(rva));
            if (section[..5].SequenceEqual(".text"u8))
            {
                textStart = rva;
                textEnd = rva + BinaryPrimitives.ReadInt32LittleEndian(section[8..]);
            }
        }

        var stubs = new List<int>();
        for (var i = textStart; i + 26 <= textEnd; i++)
        {
            if (image[i] == 0x41 && OutgoingChatText.EncoderAddress(image.AsSpan(i, 26), imageBase + i) is not null)
                stubs.Add(i);
        }
        var stub = Assert.Single(stubs);
        var encoder = (int)(OutgoingChatText.EncoderAddress(image.AsSpan(stub, 26), imageBase + stub)!.Value - imageBase);
        Assert.InRange(encoder, textStart, textEnd - 1);
        Assert.True(encoder < stub);
        var code = image.AsSpan(encoder, stub - encoder);
        foreach (var template in new[]
                 {
                     "<fixed(%d,%d,%d,%d,0,0,", // item
                     "<fixed(%d,%d,%d,0,0,0,", // quest
                     "<fixed(%d,%d,%d,1,", // custom status
                     "<fixed(%d,%d,%d,0,0)>", // status resolved by recipient
                 })
        {
            var literal = image.AsSpan().IndexOf(Encoding.UTF8.GetBytes(template + '\0'));
            Assert.True(literal >= 0, template);
            var referenced = false;
            for (var i = 0; i + 7 <= code.Length; i++)
            {
                // LEA reg,[RIP+disp32], as used for sprintf's template.
                if (code[i] is 0x48 or 0x4C && code[i + 1] == 0x8D && (code[i + 2] & 0xC7) == 0x05 &&
                    encoder + i + 7 + BinaryPrimitives.ReadInt32LittleEndian(code[(i + 3)..]) == literal)
                    referenced = true;
            }
            Assert.True(referenced, "The outgoing encoder no longer references " + template);
        }
        output.WriteLine($"PronounModule dispatch {imageBase + stub:X}; encoder {imageBase + encoder:X}; item, quest and status templates found.");

    }

    [Fact]
    public void Every_item_and_quest_can_send_its_original_name_in_every_client_language()
    {
        var path = Environment.GetEnvironmentVariable("HARMONIA_GAME");
        if (string.IsNullOrEmpty(path))
            return;
        using var game = new GameData(path, new LuminaOptions { PanicOnSheetChecksumMismatch = false });
        foreach (var language in new[] { Language.English, Language.Japanese, Language.German, Language.French })
        {
            var count = 0;
            void Check(ChatNameKind kind, uint id, string name)
            {
                if (string.IsNullOrWhiteSpace(name))
                    return;
                var type = kind == ChatNameKind.Item ? 4u : 12u;
                var encoded = OutgoingChatTextTests.Fixed(200u, type, id, 0u, 0u, 0u, "Перевод из пакета");
                var restored = OutgoingChatText.Restore(encoded, (actualKind, actualId) =>
                {
                    Assert.Equal(kind, actualKind);
                    Assert.Equal(id, actualId);
                    return name;
                }, 1023);
                Assert.NotNull(restored);
                Assert.Equal([.. OutgoingChatTextTests.Fixed(200u, type, id, 0u, 0u, 0u, name), 0], restored);
                Assert.True(SeStringCheck.IsWellFormed(restored.AsSpan(0, restored.Length - 1)));
                count++;
            }
            foreach (var item in game.GetExcelSheet<Item>(language)!)
                Check(ChatNameKind.Item, item.RowId, NameText.Plain(item.Name.Data.Span));
            foreach (var item in game.GetExcelSheet<EventItem>(language)!)
                Check(ChatNameKind.Item, item.RowId, NameText.Plain(item.Name.Data.Span));
            foreach (var quest in game.GetExcelSheet<Quest>(language)!)
                Check(ChatNameKind.Quest, quest.RowId, NameText.Plain(quest.Name.Data.Span));
            output.WriteLine($"{language}: {count} original item/event-item/quest names serialize without pack text.");

            var parameters = game.GetExcelSheet<TextCommandParam>(language)!;
            Assert.Equal("<item>", parameters.GetRow(1096).Param.ExtractText());
            Assert.Equal("<quest>", parameters.GetRow(1042).Param.ExtractText());
            Assert.Equal("<status>", parameters.GetRow(1114).Param.ExtractText());
        }
    }
}
