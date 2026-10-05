using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace AC.Dat
{
    /// <summary>A decoded image: width by height pixels, four bytes each, in R, G, B, A order.</summary>
    public sealed class RgbaImage
    {
        public RgbaImage(int width, int height, byte[] rgba)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), "An image has at least one pixel.");

            if (rgba == null || rgba.Length != width * height * 4)
                throw new ArgumentException("The pixel buffer does not match the size.", nameof(rgba));

            Width = width;
            Height = height;
            Rgba = rgba;
        }

        public int Width { get; }

        public int Height { get; }

        public byte[] Rgba { get; }

        /// <summary>
        /// The image as a PNG file. There for looking at what was decoded - a wrong channel
        /// order or palette is obvious to the eye and nearly invisible in a hex dump.
        /// </summary>
        public byte[] ToPng()
        {
            using MemoryStream png = new MemoryStream();
            png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

            byte[] header = new byte[13];
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), Width);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), Height);
            header[8] = 8;   // bits per channel
            header[9] = 6;   // truecolour with alpha
            WriteChunk(png, "IHDR", header);

            using (MemoryStream raw = new MemoryStream())
            {
                using (ZLibStream z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
                {
                    for (int y = 0; y < Height; y++)
                    {
                        z.WriteByte(0);  // no filter
                        z.Write(Rgba, y * Width * 4, Width * 4);
                    }
                }

                WriteChunk(png, "IDAT", raw.ToArray());
            }

            WriteChunk(png, "IEND", Array.Empty<byte>());
            return png.ToArray();
        }

        private static void WriteChunk(Stream stream, string type, byte[] data)
        {
            byte[] length = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
            stream.Write(length);

            byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
            stream.Write(typeBytes);
            stream.Write(data);

            uint crc = Crc32(typeBytes, 0xFFFFFFFFu);
            crc = Crc32(data, crc) ^ 0xFFFFFFFFu;
            byte[] crcBytes = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
            stream.Write(crcBytes);
        }

        private static uint Crc32(byte[] data, uint crc)
        {
            foreach (byte b in data)
            {
                crc ^= b;
                for (int k = 0; k < 8; k++)
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }

            return crc;
        }
    }

    /// <summary>
    /// Decodes the client's images - the 0x06 files in the portal archive, which hold every
    /// icon and every piece of the game's own interface art.
    /// </summary>
    /// <remarks>
    /// The layout is: id, an unknown word, width, height, pixel format, data length, the
    /// data, and - for the two paletted formats only - the id of the palette to read the
    /// indices through. The formats are Direct3D's, numbered as D3DFORMAT numbers them, with
    /// the DXT formats as their four-character codes.
    ///
    /// <para>
    /// Multi-byte pixels are little-endian, as Direct3D stores them, so an "R8G8B8" pixel
    /// is blue, green, red in memory. Decode failures are reported, not thrown: an image the
    /// overlay cannot draw is a missing picture, not a reason to stop.
    /// </para>
    /// </remarks>
    public static class DatImage
    {
        public const uint R8G8B8 = 20;
        public const uint A8R8G8B8 = 21;
        public const uint X8R8G8B8 = 22;
        public const uint R5G6B5 = 23;
        public const uint X1R5G5B5 = 24;
        public const uint A1R5G5B5 = 25;
        public const uint A4R4G4B4 = 26;
        public const uint A8 = 28;
        public const uint P8 = 41;
        public const uint Index16 = 101;
        public const uint LandscapeR8G8B8 = 243;
        public const uint LandscapeAlpha = 244;
        public const uint RawJpeg = 500;
        public const uint Dxt1 = 0x31545844;
        public const uint Dxt3 = 0x33545844;
        public const uint Dxt5 = 0x35545844;

        /// <summary>No real interface image is anywhere near this; a bigger one is a misread.</summary>
        private const int MaxSide = 4096;

        /// <summary>The header fields, for callers that want to report what they found.</summary>
        public readonly struct Header
        {
            public Header(uint id, int width, int height, uint format, int length, uint paletteId)
            {
                Id = id;
                Width = width;
                Height = height;
                Format = format;
                Length = length;
                PaletteId = paletteId;
            }

            public uint Id { get; }
            public int Width { get; }
            public int Height { get; }
            public uint Format { get; }
            public int Length { get; }
            public uint PaletteId { get; }

            public override string ToString() => $"0x{Id:X8} {Width}x{Height} format {FormatName(Format)} ({Length} bytes)"
                + (PaletteId != 0 ? $" palette 0x{PaletteId:X8}" : string.Empty);
        }

        public static string FormatName(uint format) => format switch
        {
            R8G8B8 => "R8G8B8",
            A8R8G8B8 => "A8R8G8B8",
            X8R8G8B8 => "X8R8G8B8",
            R5G6B5 => "R5G6B5",
            X1R5G5B5 => "X1R5G5B5",
            A1R5G5B5 => "A1R5G5B5",
            A4R4G4B4 => "A4R4G4B4",
            A8 => "A8",
            P8 => "P8",
            Index16 => "INDEX16",
            LandscapeR8G8B8 => "LSCAPE_R8G8B8",
            LandscapeAlpha => "LSCAPE_ALPHA",
            RawJpeg => "JPEG",
            Dxt1 => "DXT1",
            Dxt3 => "DXT3",
            Dxt5 => "DXT5",
            _ => format.ToString(),
        };

        public static bool TryReadHeader(ReadOnlySpan<byte> data, out Header header, out int pixelsOffset)
        {
            header = default;
            pixelsOffset = 0;

            if (data.Length < 24)
                return false;

            uint id = BinaryPrimitives.ReadUInt32LittleEndian(data);
            int width = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(8));
            int height = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(12));
            uint format = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(16));
            int length = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(20));

            if (width <= 0 || height <= 0 || width > MaxSide || height > MaxSide || length < 0 || 24L + length > data.Length)
                return false;

            uint palette = 0;
            if ((format == P8 || format == Index16) && 24L + length + 4 <= data.Length)
                palette = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(24 + length));

            header = new Header(id, width, height, format, length, palette);
            pixelsOffset = 24;
            return true;
        }

        /// <summary>
        /// Decodes one image file. <paramref name="palettes"/> answers a palette id with its
        /// colours as 0xAARRGGBB, and is only asked for the paletted formats.
        /// </summary>
        public static bool TryDecode(ReadOnlySpan<byte> data, Func<uint, IReadOnlyList<uint>> palettes, out RgbaImage image, out string error)
        {
            image = null;

            if (!TryReadHeader(data, out Header header, out int offset))
            {
                error = "not an image file, or a truncated one";
                return false;
            }

            ReadOnlySpan<byte> pixels = data.Slice(offset, header.Length);
            int w = header.Width;
            int h = header.Height;
            int count = w * h;
            byte[] rgba = new byte[count * 4];

            switch (header.Format)
            {
                case R8G8B8:
                case LandscapeR8G8B8:
                    if (!Need(pixels, count * 3, out error))
                        return false;
                    for (int i = 0; i < count; i++)
                        PutChannels(rgba, i, pixels[i * 3 + 2], pixels[i * 3 + 1], pixels[i * 3], 255);
                    break;

                case A8R8G8B8:
                case X8R8G8B8:
                    if (!Need(pixels, count * 4, out error))
                        return false;
                    for (int i = 0; i < count; i++)
                    {
                        byte a = header.Format == X8R8G8B8 ? (byte)255 : pixels[i * 4 + 3];
                        PutChannels(rgba, i, pixels[i * 4 + 2], pixels[i * 4 + 1], pixels[i * 4], a);
                    }
                    break;

                case R5G6B5:
                    if (!Need(pixels, count * 2, out error))
                        return false;
                    for (int i = 0; i < count; i++)
                    {
                        ushort v = BinaryPrimitives.ReadUInt16LittleEndian(pixels.Slice(i * 2));
                        PutChannels(rgba, i, Expand(v >> 11, 5), Expand((v >> 5) & 0x3F, 6), Expand(v & 0x1F, 5), 255);
                    }
                    break;

                case X1R5G5B5:
                case A1R5G5B5:
                    if (!Need(pixels, count * 2, out error))
                        return false;
                    for (int i = 0; i < count; i++)
                    {
                        ushort v = BinaryPrimitives.ReadUInt16LittleEndian(pixels.Slice(i * 2));
                        byte a = header.Format == X1R5G5B5 || (v & 0x8000) != 0 ? (byte)255 : (byte)0;
                        PutChannels(rgba, i, Expand((v >> 10) & 0x1F, 5), Expand((v >> 5) & 0x1F, 5), Expand(v & 0x1F, 5), a);
                    }
                    break;

                case A4R4G4B4:
                    if (!Need(pixels, count * 2, out error))
                        return false;
                    for (int i = 0; i < count; i++)
                    {
                        ushort v = BinaryPrimitives.ReadUInt16LittleEndian(pixels.Slice(i * 2));
                        PutChannels(rgba, i, Expand((v >> 8) & 0xF, 4), Expand((v >> 4) & 0xF, 4), Expand(v & 0xF, 4), Expand(v >> 12, 4));
                    }
                    break;

                case A8:
                case LandscapeAlpha:
                    if (!Need(pixels, count, out error))
                        return false;
                    for (int i = 0; i < count; i++)
                        PutChannels(rgba, i, 255, 255, 255, pixels[i]);
                    break;

                case P8:
                case Index16:
                {
                    int size = header.Format == P8 ? 1 : 2;
                    if (!Need(pixels, count * size, out error))
                        return false;

                    IReadOnlyList<uint> palette = header.PaletteId != 0 ? palettes?.Invoke(header.PaletteId) : null;
                    if (palette == null || palette.Count == 0)
                    {
                        error = $"palette 0x{header.PaletteId:X8} could not be read";
                        return false;
                    }

                    for (int i = 0; i < count; i++)
                    {
                        int index = size == 1 ? pixels[i] : BinaryPrimitives.ReadUInt16LittleEndian(pixels.Slice(i * 2));
                        uint c = index < palette.Count ? palette[index] : 0;
                        PutChannels(rgba, i, (byte)(c >> 16), (byte)(c >> 8), (byte)c, (byte)(c >> 24));
                    }
                    break;
                }

                case Dxt1:
                case Dxt3:
                case Dxt5:
                    if (!DecodeDxt(pixels, w, h, header.Format, rgba, out error))
                        return false;
                    break;

                default:
                    error = $"pixel format {FormatName(header.Format)} is not decoded";
                    return false;
            }

            image = new RgbaImage(w, h, rgba);
            error = null;
            return true;
        }

        private static bool Need(ReadOnlySpan<byte> pixels, int bytes, out string error)
        {
            if (pixels.Length >= bytes)
            {
                error = null;
                return true;
            }

            error = $"{pixels.Length} bytes of pixels where {bytes} were needed";
            return false;
        }

        private static void Put(byte[] rgba, int index, byte r, byte g, byte b, byte a)
        {
            rgba[index * 4] = r;
            rgba[index * 4 + 1] = g;
            rgba[index * 4 + 2] = b;
            rgba[index * 4 + 3] = a;
        }

        private static void PutChannels(byte[] rgba, int index, int r, int g, int b, int a)
            => Put(rgba, index, (byte)r, (byte)g, (byte)b, (byte)a);

        /// <summary>Widens an n-bit channel to eight, so full scale stays full scale.</summary>
        private static int Expand(int value, int bits) => (value << (8 - bits)) | (value >> (2 * bits - 8 < 0 ? 0 : 2 * bits - 8));

        private static bool DecodeDxt(ReadOnlySpan<byte> data, int width, int height, uint format, byte[] rgba, out string error)
        {
            int blocksWide = (width + 3) / 4;
            int blocksHigh = (height + 3) / 4;
            int blockSize = format == Dxt1 ? 8 : 16;

            if (!Need(data, blocksWide * blocksHigh * blockSize, out error))
                return false;

            Span<uint> colours = stackalloc uint[4];
            Span<byte> alphas = stackalloc byte[8];

            for (int by = 0; by < blocksHigh; by++)
            {
                for (int bx = 0; bx < blocksWide; bx++)
                {
                    ReadOnlySpan<byte> block = data.Slice((by * blocksWide + bx) * blockSize, blockSize);
                    ReadOnlySpan<byte> colourBlock = format == Dxt1 ? block : block.Slice(8);

                    ushort c0 = BinaryPrimitives.ReadUInt16LittleEndian(colourBlock);
                    ushort c1 = BinaryPrimitives.ReadUInt16LittleEndian(colourBlock.Slice(2));
                    uint indices = BinaryPrimitives.ReadUInt32LittleEndian(colourBlock.Slice(4));

                    colours[0] = Rgb565(c0, 255);
                    colours[1] = Rgb565(c1, 255);

                    if (format != Dxt1 || c0 > c1)
                    {
                        colours[2] = Mix(colours[0], colours[1], 2, 1, 3);
                        colours[3] = Mix(colours[0], colours[1], 1, 2, 3);
                    }
                    else
                    {
                        colours[2] = Mix(colours[0], colours[1], 1, 1, 2);
                        colours[3] = 0;  // transparent black
                    }

                    if (format == Dxt5)
                    {
                        byte a0 = block[0];
                        byte a1 = block[1];
                        alphas[0] = a0;
                        alphas[1] = a1;
                        if (a0 > a1)
                        {
                            for (int k = 1; k < 7; k++)
                                alphas[k + 1] = (byte)(((7 - k) * a0 + k * a1) / 7);
                        }
                        else
                        {
                            for (int k = 1; k < 5; k++)
                                alphas[k + 1] = (byte)(((5 - k) * a0 + k * a1) / 5);
                            alphas[6] = 0;
                            alphas[7] = 255;
                        }
                    }

                    ulong alphaBits = 0;
                    if (format == Dxt5)
                    {
                        for (int k = 0; k < 6; k++)
                            alphaBits |= (ulong)block[2 + k] << (8 * k);
                    }

                    for (int py = 0; py < 4; py++)
                    {
                        for (int px = 0; px < 4; px++)
                        {
                            int x = bx * 4 + px;
                            int y = by * 4 + py;
                            if (x >= width || y >= height)
                                continue;

                            int pixel = py * 4 + px;
                            uint colour = colours[(int)((indices >> (pixel * 2)) & 3)];
                            byte alpha = (byte)(colour >> 24);

                            if (format == Dxt3)
                            {
                                int nibble = (block[pixel / 2] >> ((pixel & 1) * 4)) & 0xF;
                                alpha = (byte)(nibble * 17);
                            }
                            else if (format == Dxt5)
                            {
                                alpha = alphas[(int)((alphaBits >> (pixel * 3)) & 7)];
                            }

                            Put(rgba, y * width + x, (byte)(colour >> 16), (byte)(colour >> 8), (byte)colour, alpha);
                        }
                    }
                }
            }

            error = null;
            return true;
        }

        private static uint Rgb565(ushort v, byte alpha)
            => ((uint)alpha << 24) | ((uint)Expand(v >> 11, 5) << 16) | ((uint)Expand((v >> 5) & 0x3F, 6) << 8) | (uint)Expand(v & 0x1F, 5);

        private static uint Mix(uint a, uint b, int wa, int wb, int total)
        {
            uint Channel(int shift) => (uint)((((a >> shift) & 0xFF) * wa + ((b >> shift) & 0xFF) * wb) / total);
            return 0xFF000000u | (Channel(16) << 16) | (Channel(8) << 8) | Channel(0);
        }
    }
}
