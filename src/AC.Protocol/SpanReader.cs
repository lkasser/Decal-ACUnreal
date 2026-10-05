using System;
using System.Buffers.Binary;
using System.Text;

namespace AC.Protocol
{
    /// <summary>
    /// A forward-only, bounds-checked reader over a byte span.
    /// </summary>
    /// <remarks>
    /// Every read is a <c>Try</c>: the protocol is parsed from bytes that arrive over
    /// UDP from an untrusted peer, so running off the end is an expected outcome and
    /// not an exceptional one. A decoder that checks each read reports "not a message
    /// I can read" instead of throwing halfway through mutating world state.
    ///
    /// A <c>ref struct</c>, so it cannot outlive the span it reads and costs no
    /// allocation.
    /// </remarks>
    public ref struct SpanReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _offset;

        public SpanReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _offset = 0;
        }

        /// <summary>Bytes consumed so far.</summary>
        public int Offset => _offset;

        /// <summary>Bytes left unread.</summary>
        public int Remaining => _data.Length - _offset;

        public bool IsAtEnd => _offset >= _data.Length;

        /// <summary>The bytes consumed so far, which is what a checksum covers.</summary>
        public ReadOnlySpan<byte> Consumed => _data.Slice(0, _offset);

        public bool TryReadBytes(int count, out ReadOnlySpan<byte> value)
        {
            if (count < 0 || count > Remaining)
            {
                value = default;
                return false;
            }

            value = _data.Slice(_offset, count);
            _offset += count;
            return true;
        }

        /// <summary>Reads without consuming, for a field another reader will re-read.</summary>
        public bool TryPeekBytes(int count, out ReadOnlySpan<byte> value)
        {
            if (count < 0 || count > Remaining)
            {
                value = default;
                return false;
            }

            value = _data.Slice(_offset, count);
            return true;
        }

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
            if (Remaining < 4)
            {
                value = 0;
                return false;
            }

            value = BinaryPrimitives.ReadInt32LittleEndian(_data.Slice(_offset));
            _offset += 4;
            return true;
        }

        public bool TryReadUInt64(out ulong value)
        {
            if (Remaining < 8)
            {
                value = 0;
                return false;
            }

            value = BinaryPrimitives.ReadUInt64LittleEndian(_data.Slice(_offset));
            _offset += 8;
            return true;
        }

        public bool TryReadSingle(out float value)
        {
            if (!TryReadInt32(out int bits))
            {
                value = 0f;
                return false;
            }

            value = BitConverter.Int32BitsToSingle(bits);
            return true;
        }

        public bool TryReadDouble(out double value)
        {
            if (!TryReadUInt64(out ulong bits))
            {
                value = 0d;
                return false;
            }

            value = BitConverter.Int64BitsToDouble((long)bits);
            return true;
        }

        /// <summary>
        /// Reads the protocol's length-prefixed string: a 16-bit length, that many
        /// bytes, then padding so the whole field (prefix included) is a multiple of
        /// four bytes long.
        /// </summary>
        /// <remarks>
        /// The padding is relative to the field's own start, not to the stream. The
        /// two coincide whenever the field starts on a 4-byte boundary, which is where
        /// every writer puts it - but the rule is the field's, so that is the one
        /// implemented. Text is Windows-1252, which Latin-1 matches for everything a
        /// game name is likely to contain.
        /// </remarks>
        public bool TryReadString(out string value)
        {
            value = null;

            if (!TryReadUInt16(out ushort length))
                return false;

            if (!TryReadBytes(length, out ReadOnlySpan<byte> bytes))
                return false;

            value = AcEncoding.Text.GetString(bytes);

            int fieldLength = 2 + length;
            int padding = (4 - (fieldLength % 4)) % 4;

            return TrySkip(padding);
        }

        /// <summary>
        /// Advances to the next multiple of <paramref name="boundary"/>, relative to
        /// the start of this reader's span.
        /// </summary>
        public bool TryAlign(int boundary)
        {
            if (boundary <= 0)
                return false;

            int overshoot = _offset % boundary;
            if (overshoot == 0)
                return true;

            return TrySkip(boundary - overshoot);
        }
    }
}
