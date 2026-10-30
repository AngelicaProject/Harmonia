using System.Text;
using Harmonia.Packs.Hpk;
using Harmonia.Runtime;
using Xunit;

namespace Harmonia.Tests;

public sealed class OutgoingChatTextTests
{
    [Theory]
    [InlineData(100, "Potion")]
    [InlineData(1_000_100, "Potion \uE03C")]
    [InlineData(500_100, "Potion \uE03D")]
    [InlineData(2_000_100, "Event item")]
    public void Item_wire_name_is_original_and_all_numeric_fields_survive(uint id, string expected)
    {
        var sent = Fixed(200u, 4u, id, 3u, 0u, 0u, "Зелье");
        var result = OutgoingChatText.Restore(sent, (kind, rawId) =>
        {
            Assert.Equal(ChatNameKind.Item, kind);
            Assert.Equal(id, rawId);
            return id >= 2_000_000 ? "Event item" : "Potion";
        }, 1023);
        Assert.Equal([.. Fixed(200u, 4u, id, 3u, 0u, 0u, expected), 0], result);
        Assert.NotNull(result);
        Assert.True(SeStringCheck.IsWellFormed(result.AsSpan(0, result.Length - 1)));
    }

    [Fact]
    public void Quest_wire_title_is_original_in_a_line_with_multiple_links_and_russian_prose()
    {
        byte[] before = [.. "Пойдём "u8, .. Fixed(200u, 12u, 65537u, 0u, 0u, 0u, "Квест"),
            .. " с "u8, .. Fixed(200u, 4u, 100u, 2u, 0u, 0u, "Зелье"), .. "!"u8];
        byte[] expected = [.. "Пойдём "u8, .. Fixed(200u, 12u, 65537u, 0u, 0u, 0u, "Quest"),
            .. " с "u8, .. Fixed(200u, 4u, 100u, 2u, 0u, 0u, "Potion"), .. "!"u8, 0];
        Assert.Equal(expected, OutgoingChatText.Restore(before, (kind, _) => kind == ChatNameKind.Quest ? "Quest" : "Potion", 1023));
    }

    [Fact]
    public void Status_sends_only_its_id_without_either_translated_title_or_description()
    {
        var sent = Fixed(200u, 10u, 123u, 1u, "Состояние", "Описание <и прочий текст>");
        Assert.Equal([.. Fixed(200u, 10u, 123u, 0u, 0u), 0],
            OutgoingChatText.Restore(sent, (_, _) => throw new InvalidOperationException(), 1023));
    }

    [Fact]
    public void Original_names_support_multibyte_text_and_long_string_lengths()
    {
        var name = string.Concat(Enumerable.Repeat("ポーション", 30));
        Assert.Equal([.. Fixed(200u, 4u, 100u, 1u, 0u, 0u, name), 0],
            OutgoingChatText.Restore(Fixed(200u, 4u, 100u, 1u, 0u, 0u, "Зелье"), (_, _) => name, 1023));
    }

    [Fact]
    public void Numeric_map_npc_auto_translate_player_and_unknown_payloads_are_untouched()
    {
        byte[] safe = [.. "Русский текст"u8, .. Fixed(200u, 3u, 100u, 200u, 300u, 400u, 0u, 0u),
            .. Fixed(200u, 6u, 100u), .. Fixed(200u, 1u, 0u, "Player Name"),
            .. Fixed(200u, 7u, "Имя питомца"), .. Fixed(200u, 99u, "Неизвестно"),
            0x02, 0x2E, 0x03, 0x64, 0x65, 0x03];
        Assert.Null(OutgoingChatText.Restore(safe, (_, _) => throw new InvalidOperationException(), 1023));

        var status = Fixed(200u, 10u, 5u, 1u, "Состояние", "Описание");
        Assert.Equal([.. safe, .. Fixed(200u, 10u, 5u, 0u, 0u), 0],
            OutgoingChatText.Restore([.. safe, .. status], (_, _) => null, 1023));
    }

    [Fact]
    public void Missing_rows_unrecognized_shapes_and_malformed_strings_are_unchanged()
    {
        Assert.Null(OutgoingChatText.Restore(Fixed(200u, 4u, 1u, 1u, 0u, 0u, "Зелье"), (_, _) => null, 1023));
        Assert.Null(OutgoingChatText.Restore(Fixed(200u, 4u, 1u, 1u, 9u, 0u, "Зелье"), (_, _) => "Potion", 1023));
        Assert.Null(OutgoingChatText.Restore(Fixed(200u, 12u, 1u, 0u, 0u, 0u), (_, _) => "Quest", 1023));
        Assert.Null(OutgoingChatText.Restore([0x02, 0x2E, 0xFF], (_, _) => "Potion", 1023));
        Assert.Null(OutgoingChatText.Restore([.. "Текст"u8, 0], (_, _) => "Potion", 1023));
        Assert.Null(OutgoingChatText.Restore("Просто текст"u8, (_, _) => "Potion", 1023));
    }

    [Fact]
    public void Repeated_encoding_is_idempotent_and_does_not_truncate_following_prose()
    {
        var sent = Fixed(200u, 4u, 100u, 1u, 0u, 0u, "Зелье");
        var result = OutgoingChatText.Restore(sent, (_, _) => "Potion", 1023)!;
        Assert.Null(OutgoingChatText.Restore(result.AsSpan(0, result.Length - 1), (_, _) => "Potion", 1023));
        Assert.Throws<InvalidOperationException>(() => OutgoingChatText.Restore(sent, (_, _) => new string('a', 1023), 1023));
    }

    [Fact]
    public void Encoder_branch_preserves_the_native_flags_argument_and_rejects_unknown_dispatch()
    {
        byte[] stub = [0x41, 0x8B, 0xC1, 0x4D, 0x85, 0xC0, 0x74, 0x0D,
            0x4C, 0x8B, 0x4C, 0x24, 0x28, 0x44, 0x8B, 0xC0, 0xE9, 0xEB, 0xFF, 0xFF, 0xFF,
            0xE9, 0, 0, 0, 0];
        Assert.Equal(0x1000, OutgoingChatText.EncoderAddress(stub, 0x1000));
        stub[8] = 0x90;
        Assert.Null(OutgoingChatText.EncoderAddress(stub, 0x1000));
    }

    // Independent fixture writer: wire macros have integer expressions and
    // length-prefixed UTF-8 strings, never a C# character count as a length.
    internal static byte[] Fixed(params object[] args)
    {
        var body = new List<byte>();
        foreach (var arg in args)
        {
            if (arg is uint number)
                body.AddRange(UInt(number));
            else
            {
                var text = Encoding.UTF8.GetBytes((string)arg);
                body.Add(0xFF);
                body.AddRange(UInt((uint)text.Length));
                body.AddRange(text);
            }
        }
        return [0x02, 0x2E, .. UInt((uint)body.Count), .. body, 0x03];
    }

    private static byte[] UInt(uint value)
    {
        if (value < 0xCF)
            return [(byte)(value + 1)];
        var bytes = new List<byte>();
        byte mask = 0xF0;
        for (var shift = 24; shift >= 0; shift -= 8)
        {
            var part = (byte)(value >> shift);
            if (part == 0)
                continue;
            mask |= (byte)(1 << (shift / 8));
            bytes.Add(part);
        }
        return [(byte)(mask - 1), .. bytes];
    }
}
