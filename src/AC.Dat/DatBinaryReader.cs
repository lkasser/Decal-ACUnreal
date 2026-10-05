using System;
using System.Buffers.Binary;
using System.Text;

namespace AC.Dat
{
    /// <summary>
    /// A bounds-checked reader for the encodings the DAT files use.
    /// </summary>
    /// <remarks>
    /// Distinct from the network reader: these are on-disk formats with their own
    /// conventions - byte-length strings, nibble-swapped strings, ids packed with an
    /// implied type, and four-byte alignment after each string. Reads still return
    /// false rather than throwing, because a DAT can be truncated or from a client
    /// version that lays a record out differently.
    /// </remarks>
    public ref struct DatBinaryReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _offset;

        public DatBinaryReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _offset = 0;
        }

        public int Offset => _offset;

        public int Remaining => _data.Length - _offset;

        public bool TrySkip(int count)
        {
            if (count < 0 || count > Remaining)
                return false;

            _offset += count;
            return true;
        }

        public bool TryReadByte(out byte value)
        {
            if (Remaining < 1)
            {
                value = 0;
                return false;
            }

            value = _data[_offset++];
            return true;
        }

        public bool TryReadUInt16(out ushort value)
        {
            if (Remaining < 2)
            {
                value = 0;
                return false;
            }

            value = BinaryPrimitives.ReadUInt16LittleEndian(_data.Slice(_offset));
            _offset += 2;
            return true;
        }

        public bool TryReadUInt32(out uint value)
        {
            if (Remaining < 4)
            {
                value = 0;
                return false;
            }

            value = BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(_offset));
            _offset += 4;
            return true;
        }

        public bool TryReadInt32(out int value)
        {
            if (!TryReadUInt32(out uint raw))
            {
                value = 0;
                return false;
            }

            value = unchecked((int)raw);
            return true;
        }

        public bool TryReadSingle(out float value)
        {
            if (!TryReadUInt32(out uint bits))
            {
                value = 0f;
                return false;
            }

            value = BitConverter.Int32BitsToSingle(unchecked((int)bits));
            return true;
        }

        public bool TryReadDouble(out double value)
        {
            if (Remaining < 8)
            {
                value = 0d;
                return false;
            }

            long bits = BinaryPrimitives.ReadInt64LittleEndian(_data.Slice(_offset));
            _offset += 8;
            value = BitConverter.Int64BitsToDouble(bits);
            return true;
        }

        /// <summary>Advances to the next four-byte boundary of the buffer.</summary>
        public bool TryAlign()
        {
            int overshoot = _offset % 4;
            return overshoot == 0 || TrySkip(4 - overshoot);
        }

        /// <summary>A 16-bit length followed by that many bytes of Windows-1252 text.</summary>
        public bool TryReadString(out string value)
        {
            value = null;

            if (!TryReadUInt16(out ushort length))
                return false;

            if (length > Remaining)
                return false;

            value = AcEncoding.Text.GetString(_data.Slice(_offset, length));
            _offset += length;
            return true;
        }

        /// <summary>
        /// A string whose bytes have had their nibbles swapped.
        /// </summary>
        /// <remarks>
        /// Not encryption - just enough to keep spell names out of a plain strings
        /// dump of the file. The transform is its own inverse.
        /// </remarks>
        public bool TryReadObfuscatedString(out string value)
        {
            value = null;

            if (!TryReadUInt16(out ushort length))
                return false;

            if (length > Remaining)
                return false;

            byte[] text = new byte[length];
            for (int i = 0; i < length; i++)
            {
                byte b = _data[_offset + i];
                text[i] = (byte)((b >> 4) | (b << 4));
            }

            _offset += length;
            value = AcEncoding.Text.GetString(text);
            return true;
        }

        /// <summary>
        /// A data id stored without its type byte: one word normally, two when the
        /// high bit is set, with the type added back on.
        /// </summary>
        public bool TryReadPackedIdOfType(uint type, out uint value)
        {
            value = 0;

            if (!TryReadUInt16(out ushort first))
                return false;

            if ((first & 0x8000) == 0)
            {
                value = type + first;
                return true;
            }

            if (!TryReadUInt16(out ushort second))
                return false;

            value = type + (uint)(((first & 0x3FFF) << 16) | second);
            return true;
        }

        /// <summary>
        /// The header of a packed hash table: an entry count and a bucket count, of
        /// which only the first matters for reading.
        /// </summary>
        public bool TryReadHashTableHeader(out int count)
        {
            count = 0;

            if (!TryReadUInt16(out ushort entries))
                return false;

            if (!TryReadUInt16(out _))
                return false;

            count = entries;
            return true;
        }
    }
}
