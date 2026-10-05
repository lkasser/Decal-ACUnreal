using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace AC.Host.Actions
{
    /// <summary>
    /// Builds a message payload. Shared by the actions sent to the server and the lines
    /// shown in the game, which are the same bytes travelling in opposite directions.
    /// </summary>
    internal sealed class PayloadWriter
    {
        private readonly List<byte> _bytes = new List<byte>(32);

        internal PayloadWriter UInt32(uint value)
        {
            Span<byte> buffer = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
            foreach (byte b in buffer)
                _bytes.Add(b);
            return this;
        }

        internal PayloadWriter Int32(int value) => UInt32(unchecked((uint)value));

        internal PayloadWriter Byte(byte value)
        {
            _bytes.Add(value);
            return this;
        }

        internal PayloadWriter Bytes(byte[] values)
        {
            _bytes.AddRange(values);
            return this;
        }

        internal PayloadWriter UInt16(ushort value)
        {
            Span<byte> buffer = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
            _bytes.Add(buffer[0]);
            _bytes.Add(buffer[1]);
            return this;
        }

        internal PayloadWriter Single(float value)
            => UInt32(BitConverter.SingleToUInt32Bits(value));

        /// <summary>
        /// A 16-bit length, the bytes, then padding so the whole field is a
        /// multiple of four - the same rule the readers apply.
        /// </summary>
        internal PayloadWriter String(string value)
        {
            byte[] text = AC.Protocol.AcEncoding.Text.GetBytes(value);

            Span<byte> length = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(length, (ushort)text.Length);
            _bytes.Add(length[0]);
            _bytes.Add(length[1]);
            _bytes.AddRange(text);

            int padding = (4 - ((2 + text.Length) % 4)) % 4;
            for (int i = 0; i < padding; i++)
                _bytes.Add(0);

            return this;
        }

        internal byte[] ToArray() => _bytes.ToArray();
    }
}
