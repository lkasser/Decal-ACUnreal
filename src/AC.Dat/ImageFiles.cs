using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace AC.Dat
{
    /// <summary>
    /// Decodes the two ordinary image formats the Decal look is drawn from: the PNGs Virindi
    /// View Service embeds for its themes, and the BMPs Decal installs beside itself.
    /// </summary>
    /// <remarks>
    /// Only as much of each format as those files use, plus the common variants a replacement
    /// saved from another tool might plausibly be in. System.Drawing would read both, but on
    /// .NET it is Windows-only and would pull GDI+ into a host that otherwise has no use for
    /// it. Failures are reported, not thrown: a picture that cannot be read is a missing
    /// picture, not a reason to stop.
    /// </remarks>
    public static class ImageFiles
    {
        /// <summary>No theme image is anywhere near this; a bigger one is a misread or a hostile file.</summary>
        private const int MaxSide = 8192;

        private static ReadOnlySpan<byte> PngSignature => new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        public static bool IsPng(ReadOnlySpan<byte> data) => data.Length >= 8 && data.Slice(0, 8).SequenceEqual(PngSignature);

        public static bool IsBmp(ReadOnlySpan<byte> data) => data.Length >= 2 && data[0] == (byte)'B' && data[1] == (byte)'M';

        /// <summary>
        /// Decodes a PNG or a BMP, whichever the bytes say it is. The content decides rather
        /// than the file name, because a renamed file is still the format it was saved as.
        /// </summary>
        public static bool TryDecode(ReadOnlySpan<byte> data, out RgbaImage image, out string error)
        {
            if (IsPng(data))
                return TryDecodePng(data, out image, out error);

            if (IsBmp(data))
                return TryDecodeBmp(data, out image, out error);

            image = null;
            error = "neither a PNG nor a BMP file";
            return false;
        }

        /// <summary>
        /// A copy of <paramref name="image"/> in which every pixel exactly equal to the given
        /// colour is fully transparent.
        /// </summary>
        /// <remarks>
        /// Decal and Virindi View Service predate alpha in their artwork and mark the see-through
        /// parts with pure cyan (0, 255, 255) instead. Only an exact match is keyed, as they did
        /// it: a near-cyan pixel is a deliberate colour.
        ///
        /// <para>
        /// A keyed pixel's colour is replaced by the average of its opaque neighbours, or black
        /// if it has none. The overlay draws with linear filtering and straight alpha, which
        /// blends a transparent pixel's colour into the edge of the opaque one beside it; left
        /// cyan, every tab and switch would have drawn with a cyan fringe.
        /// </para>
        /// </remarks>
        public static RgbaImage WithColourKey(RgbaImage image, byte r, byte g, byte b)
        {
            if (image == null)
                throw new ArgumentNullException(nameof(image));

            byte[] source = image.Rgba;
            byte[] rgba = (byte[])source.Clone();
            int w = image.Width;
            int h = image.Height;

            bool IsKey(int index) => source[index] == r && source[index + 1] == g && source[index + 2] == b;

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    if (!IsKey(i))
                        continue;

                    int sr = 0, sg = 0, sb = 0, n = 0;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx;
                            int ny = y + dy;
                            if ((dx == 0 && dy == 0) || nx < 0 || ny < 0 || nx >= w || ny >= h)
                                continue;

                            int j = (ny * w + nx) * 4;
                            if (IsKey(j) || source[j + 3] == 0)
                                continue;

                            sr += source[j];
                            sg += source[j + 1];
                            sb += source[j + 2];
                            n++;
                        }
                    }

                    rgba[i] = n > 0 ? (byte)(sr / n) : (byte)0;
                    rgba[i + 1] = n > 0 ? (byte)(sg / n) : (byte)0;
                    rgba[i + 2] = n > 0 ? (byte)(sb / n) : (byte)0;
                    rgba[i + 3] = 0;
                }
            }

            return new RgbaImage(image.Width, image.Height, rgba);
        }

        /// <summary>
        /// Decodes a non-interlaced PNG of up to eight bits per channel, in any of the five
        /// colour types. The chunk CRCs are not checked: a file whose checksum is wrong but
        /// whose pixels decompress is more use drawn than refused.
        /// </summary>
        public static bool TryDecodePng(ReadOnlySpan<byte> data, out RgbaImage image, out string error)
        {
            image = null;

            if (!IsPng(data))
            {
                error = "not a PNG file (the signature is wrong)";
                return false;
            }

            int offset = PngSignature.Length;
            bool haveHeader = false;
            bool ended = false;
            int width = 0;
            int height = 0;
            int bitDepth = 0;
            int colourType = 0;
            byte[] palette = null;
            byte[] transparency = null;
            using MemoryStream compressed = new MemoryStream();

            while (!ended)
            {
                if (data.Length - offset < 12)
                {
                    error = "the file ends before its IEND chunk; it is truncated";
                    return false;
                }

                uint length = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset));
                string type = Encoding.ASCII.GetString(data.Slice(offset + 4, 4));

                if (length > (uint)(data.Length - offset - 12))
                {
                    error = $"the {type} chunk runs past the end of the file; it is truncated";
                    return false;
                }

                ReadOnlySpan<byte> body = data.Slice(offset + 8, (int)length);
                offset += 12 + (int)length;

                if (!haveHeader && type != "IHDR")
                {
                    error = $"the first chunk is {type}, not IHDR";
                    return false;
                }

                switch (type)
                {
                    case "IHDR":
                        if (haveHeader)
                        {
                            error = "the file has two IHDR chunks";
                            return false;
                        }

                        if (!TryReadPngHeader(body, out width, out height, out bitDepth, out colourType, out error))
                            return false;

                        haveHeader = true;
                        break;

                    case "PLTE":
                        if (body.Length == 0 || body.Length % 3 != 0 || body.Length > 256 * 3)
                        {
                            error = $"the palette is {body.Length} bytes, which is not a whole number of colours";
                            return false;
                        }

                        palette = body.ToArray();
                        break;

                    case "tRNS":
                        transparency = body.ToArray();
                        break;

                    case "IDAT":
                        // The pixel data may be split across any number of IDAT chunks; it is
                        // one zlib stream only once they are put back together.
                        compressed.Write(body);
                        break;

                    case "IEND":
                        ended = true;
                        break;

                    default:
                        // Bit 5 of the first letter marks a chunk as safe to ignore. One that is
                        // not changes how the pixels are to be read, so guessing would be wrong.
                        if ((type[0] & 0x20) == 0)
                        {
                            error = $"the file has a {type} chunk, which this decoder does not understand and cannot skip";
                            return false;
                        }

                        break;
                }
            }

            if (compressed.Length == 0)
            {
                error = "the file has no image data (no IDAT chunk)";
                return false;
            }

            if (colourType == 3 && palette == null)
            {
                error = "a palette image has no PLTE chunk";
                return false;
            }

            int channels = colourType switch
            {
                0 => 1,
                2 => 3,
                3 => 1,
                4 => 2,
                _ => 4,
            };

            int bitsPerPixel = channels * bitDepth;
            int stride = (width * bitsPerPixel + 7) / 8;
            byte[] raw = new byte[height * (stride + 1)];

            try
            {
                compressed.Position = 0;
                using ZLibStream z = new ZLibStream(compressed, CompressionMode.Decompress, leaveOpen: true);

                int got = 0;
                while (got < raw.Length)
                {
                    int n = z.Read(raw, got, raw.Length - got);
                    if (n <= 0)
                        break;
                    got += n;
                }

                if (got < raw.Length)
                {
                    error = $"the image data decompresses to {got} bytes where {raw.Length} were needed";
                    return false;
                }
            }
            catch (InvalidDataException ex)
            {
                error = $"the image data does not decompress: {ex.Message}";
                return false;
            }

            if (!Unfilter(raw, height, stride, Math.Max(1, bitsPerPixel / 8), out error))
                return false;

            byte[] rgba = new byte[width * height * 4];

            for (int y = 0; y < height; y++)
            {
                ReadOnlySpan<byte> row = raw.AsSpan(y * (stride + 1) + 1, stride);

                for (int x = 0; x < width; x++)
                {
                    int o = (y * width + x) * 4;

                    switch (colourType)
                    {
                        case 0:
                        {
                            int sample = Sample(row, x, bitDepth);
                            byte grey = (byte)(sample * 255 / ((1 << bitDepth) - 1));
                            bool clear = transparency != null && transparency.Length >= 2
                                && sample == BinaryPrimitives.ReadUInt16BigEndian(transparency);
                            Put(rgba, o, grey, grey, grey, clear ? (byte)0 : (byte)255);
                            break;
                        }

                        case 2:
                        {
                            byte r = row[x * 3];
                            byte g = row[x * 3 + 1];
                            byte b = row[x * 3 + 2];
                            bool clear = transparency != null && transparency.Length >= 6
                                && r == BinaryPrimitives.ReadUInt16BigEndian(transparency)
                                && g == BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(2))
                                && b == BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(4));
                            Put(rgba, o, r, g, b, clear ? (byte)0 : (byte)255);
                            break;
                        }

                        case 3:
                        {
                            int index = Sample(row, x, bitDepth);
                            if (index * 3 >= palette.Length)
                            {
                                error = $"pixel ({x}, {y}) uses colour {index} of a {palette.Length / 3}-colour palette";
                                return false;
                            }

                            // tRNS may be shorter than the palette; the colours past its end are opaque.
                            byte a = transparency != null && index < transparency.Length ? transparency[index] : (byte)255;
                            Put(rgba, o, palette[index * 3], palette[index * 3 + 1], palette[index * 3 + 2], a);
                            break;
                        }

                        case 4:
                            Put(rgba, o, row[x * 2], row[x * 2], row[x * 2], row[x * 2 + 1]);
                            break;

                        default:
                            Put(rgba, o, row[x * 4], row[x * 4 + 1], row[x * 4 + 2], row[x * 4 + 3]);
                            break;
                    }
                }
            }

            image = new RgbaImage(width, height, rgba);
            error = null;
            return true;
        }

        private static bool TryReadPngHeader(ReadOnlySpan<byte> body, out int width, out int height, out int bitDepth, out int colourType, out string error)
        {
            width = height = bitDepth = colourType = 0;

            if (body.Length != 13)
            {
                error = $"the IHDR chunk is {body.Length} bytes, not 13";
                return false;
            }

            uint w = BinaryPrimitives.ReadUInt32BigEndian(body);
            uint h = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(4));
            bitDepth = body[8];
            colourType = body[9];

            if (w == 0 || h == 0 || w > MaxSide || h > MaxSide)
            {
                error = $"an image of {w}x{h} is not plausible";
                return false;
            }

            width = (int)w;
            height = (int)h;

            if (body[10] != 0 || body[11] != 0)
            {
                error = $"compression method {body[10]} or filter method {body[11]} is not the standard one";
                return false;
            }

            if (body[12] != 0)
            {
                error = "interlaced PNGs are not decoded";
                return false;
            }

            if (bitDepth == 16)
            {
                error = "16-bit PNGs are not decoded";
                return false;
            }

            bool valid = colourType switch
            {
                0 => bitDepth is 1 or 2 or 4 or 8,
                3 => bitDepth is 1 or 2 or 4 or 8,
                2 or 4 or 6 => bitDepth == 8,
                _ => false,
            };

            if (!valid)
            {
                error = $"colour type {colourType} at {bitDepth} bits is not a valid PNG combination";
                return false;
            }

            error = null;
            return true;
        }

        /// <summary>
        /// Undoes the per-row filters in place. Each row's first byte names its filter, and
        /// each filter predicts a byte from its neighbours to the left, above, and above-left
        /// - neighbours measured in whole pixels, or whole bytes below eight bits a pixel.
        /// </summary>
        private static bool Unfilter(byte[] raw, int height, int stride, int bpp, out string error)
        {
            byte[] zeros = new byte[stride];

            for (int y = 0; y < height; y++)
            {
                int start = y * (stride + 1);
                byte filter = raw[start];
                Span<byte> row = raw.AsSpan(start + 1, stride);
                ReadOnlySpan<byte> prior = y == 0 ? zeros : raw.AsSpan(start - stride, stride);

                switch (filter)
                {
                    case 0:
                        break;

                    case 1:
                        for (int i = bpp; i < stride; i++)
                            row[i] = (byte)(row[i] + row[i - bpp]);
                        break;

                    case 2:
                        for (int i = 0; i < stride; i++)
                            row[i] = (byte)(row[i] + prior[i]);
                        break;

                    case 3:
                        for (int i = 0; i < stride; i++)
                        {
                            int left = i >= bpp ? row[i - bpp] : 0;
                            row[i] = (byte)(row[i] + ((left + prior[i]) >> 1));
                        }

                        break;

                    case 4:
                        for (int i = 0; i < stride; i++)
                        {
                            int left = i >= bpp ? row[i - bpp] : 0;
                            int upLeft = i >= bpp ? prior[i - bpp] : 0;
                            row[i] = (byte)(row[i] + Paeth(left, prior[i], upLeft));
                        }

                        break;

                    default:
                        error = $"row {y} has filter type {filter}, which is not one of the five PNG defines";
                        return false;
                }
            }

            error = null;
            return true;
        }

        private static int Paeth(int a, int b, int c)
        {
            int p = a + b - c;
            int pa = Math.Abs(p - a);
            int pb = Math.Abs(p - b);
            int pc = Math.Abs(p - c);

            if (pa <= pb && pa <= pc)
                return a;

            return pb <= pc ? b : c;
        }

        /// <summary>
        /// Decodes a Windows bitmap: 1, 4 or 8 bits through a palette, or 24 or 32 bits direct,
        /// uncompressed, either way up. The header may be any of the Windows info headers from
        /// BITMAPINFOHEADER on; the fields past its first forty bytes are not needed.
        /// </summary>
        public static bool TryDecodeBmp(ReadOnlySpan<byte> data, out RgbaImage image, out string error)
        {
            image = null;

            const int FileHeaderSize = 14;

            if (!IsBmp(data) || data.Length < FileHeaderSize + 4)
            {
                error = "not a BMP file (no BM signature, or too short to hold one)";
                return false;
            }

            uint pixelOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(10));
            uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(14));

            if (headerSize == 12)
            {
                error = "OS/2 bitmaps (a 12-byte core header) are not decoded";
                return false;
            }

            if (headerSize < 40 || headerSize > data.Length - FileHeaderSize)
            {
                error = $"the info header claims {headerSize} bytes, which the file does not hold";
                return false;
            }

            ReadOnlySpan<byte> info = data.Slice(FileHeaderSize, (int)headerSize);
            int width = BinaryPrimitives.ReadInt32LittleEndian(info.Slice(4));
            int rawHeight = BinaryPrimitives.ReadInt32LittleEndian(info.Slice(8));
            int bitCount = BinaryPrimitives.ReadUInt16LittleEndian(info.Slice(14));
            uint compression = BinaryPrimitives.ReadUInt32LittleEndian(info.Slice(16));
            uint coloursUsed = BinaryPrimitives.ReadUInt32LittleEndian(info.Slice(32));

            // A negative height is how a bitmap says its rows are stored top row first.
            bool topDown = rawHeight < 0;
            long height = Math.Abs((long)rawHeight);

            if (width <= 0 || height == 0 || width > MaxSide || height > MaxSide)
            {
                error = $"an image of {width}x{rawHeight} is not plausible";
                return false;
            }

            if (compression != 0)
            {
                error = $"compression {compression} is not decoded; only uncompressed (BI_RGB) bitmaps are";
                return false;
            }

            if (bitCount is not (1 or 4 or 8 or 24 or 32))
            {
                error = $"{bitCount}-bit bitmaps are not decoded";
                return false;
            }

            // The palette follows the info header, four bytes a colour (blue, green, red, unused).
            uint[] palette = null;
            if (bitCount <= 8)
            {
                int capacity = 1 << bitCount;
                if (coloursUsed > capacity)
                {
                    error = $"the palette claims {coloursUsed} colours, more than {bitCount} bits can index";
                    return false;
                }

                int entries = coloursUsed != 0 ? (int)coloursUsed : capacity;
                int paletteStart = FileHeaderSize + (int)headerSize;

                if (data.Length - paletteStart < entries * 4)
                {
                    error = $"the {entries}-colour palette runs past the end of the file";
                    return false;
                }

                palette = new uint[entries];
                for (int i = 0; i < entries; i++)
                    palette[i] = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(paletteStart + i * 4));
            }

            // Rows are padded to four bytes. The last row's padding is not needed to decode it,
            // and some writers leave it off, so it is not demanded.
            int stride = ((width * bitCount + 31) / 32) * 4;
            int rowBytes = (width * bitCount + 7) / 8;
            long needed = (long)pixelOffset + stride * (height - 1) + rowBytes;

            if (needed > data.Length)
            {
                error = $"the pixels need {needed} bytes of file and it has {data.Length}; it is truncated";
                return false;
            }

            int h = (int)height;
            byte[] rgba = new byte[width * h * 4];

            for (int y = 0; y < h; y++)
            {
                int stored = topDown ? y : h - 1 - y;
                ReadOnlySpan<byte> row = data.Slice((int)pixelOffset + stored * stride, rowBytes);

                for (int x = 0; x < width; x++)
                {
                    int o = (y * width + x) * 4;

                    switch (bitCount)
                    {
                        case 24:
                            Put(rgba, o, row[x * 3 + 2], row[x * 3 + 1], row[x * 3], 255);
                            break;

                        case 32:
                            // The fourth byte is only alpha under BI_BITFIELDS with a mask for it;
                            // under BI_RGB it is padding, and the files in the wild fill it with junk.
                            Put(rgba, o, row[x * 4 + 2], row[x * 4 + 1], row[x * 4], 255);
                            break;

                        default:
                        {
                            int index = Sample(row, x, bitCount);
                            if (index >= palette.Length)
                            {
                                error = $"pixel ({x}, {y}) uses colour {index} of a {palette.Length}-colour palette";
                                return false;
                            }

                            uint c = palette[index];
                            Put(rgba, o, (byte)(c >> 16), (byte)(c >> 8), (byte)c, 255);
                            break;
                        }
                    }
                }
            }

            image = new RgbaImage(width, h, rgba);
            error = null;
            return true;
        }

        /// <summary>
        /// The <paramref name="index"/>th sample of a row packed <paramref name="bits"/> to a
        /// sample. Both formats pack the leftmost pixel into the highest bits of a byte.
        /// </summary>
        private static int Sample(ReadOnlySpan<byte> row, int index, int bits)
        {
            if (bits == 8)
                return row[index];

            int bit = index * bits;
            int shift = 8 - bits - (bit & 7);
            return (row[bit >> 3] >> shift) & ((1 << bits) - 1);
        }

        private static void Put(byte[] rgba, int offset, byte r, byte g, byte b, byte a)
        {
            rgba[offset] = r;
            rgba[offset + 1] = g;
            rgba[offset + 2] = b;
            rgba[offset + 3] = a;
        }
    }
}
