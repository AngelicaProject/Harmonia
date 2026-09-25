using System.Buffers.Binary;

namespace Harmonia.Fonts;

// A game font atlas texture (common/font/font*.tex): an 80-byte header, then
// one 1024×1024 surface of format 0x1440, 16 bits per pixel. Every 4-bit
// channel is a separate glyph page. A glyph's texIndex % 4 selects the
// channel, which lives at bit 8, 4, 0 and 12 for channels 0–3 (the order
// the game's own glyphs use).
public sealed class FontTexture
{
    public const int HeaderSize = 80;
    public const uint Format = 0x1440;
    public const int Channels = 4;

    private static readonly int[] ChannelShift = [8, 4, 0, 12];

    private readonly byte[] bytes;

    private FontTexture(byte[] bytes, int width, int height)
    {
        this.bytes = bytes;
        Width = width;
        Height = height;
    }

    public int Width { get; }
    public int Height { get; }

    public static FontTexture Parse(byte[] file)
    {
        if (file.Length < HeaderSize)
            throw new FormatException("Texture is too small.");

        var format = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4));
        var width = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(8));
        var height = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(10));
        var surface = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(28));
        if (format != Format || width == 0 || height == 0 || surface != HeaderSize ||
            file.Length < HeaderSize + (width * height * 2))
            throw new FormatException("Unexpected font texture format.");

        return new FontTexture((byte[])file.Clone(), width, height);
    }

    public byte Get(int channel, int x, int y)
    {
        var value = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(Offset(x, y)));
        return (byte)((value >> ChannelShift[channel]) & 0xF);
    }

    public void Set(int channel, int x, int y, byte value)
    {
        var at = bytes.AsSpan(Offset(x, y));
        var shift = ChannelShift[channel];
        var pixel = BinaryPrimitives.ReadUInt16LittleEndian(at);
        pixel = (ushort)((pixel & ~(0xF << shift)) | ((value & 0xF) << shift));
        BinaryPrimitives.WriteUInt16LittleEndian(at, pixel);
    }

    // True when no pixel of the channel is set.
    public bool IsChannelEmpty(int channel)
    {
        var mask = (ushort)(0xF << ChannelShift[channel]);
        var surface = bytes.AsSpan(HeaderSize, Width * Height * 2);
        for (var i = 0; i < surface.Length; i += 2)
        {
            if ((BinaryPrimitives.ReadUInt16LittleEndian(surface[i..]) & mask) != 0)
                return false;
        }

        return true;
    }

    public byte[] ToArray() => (byte[])bytes.Clone();

    private int Offset(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
            throw new ArgumentOutOfRangeException(nameof(x));
        return HeaderSize + (((y * Width) + x) * 2);
    }
}
