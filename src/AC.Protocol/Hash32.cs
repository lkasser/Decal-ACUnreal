using System;
using System.Buffers.Binary;

namespace AC.Protocol
{
    /// <summary>
    /// The checksum the AC protocol uses for packet headers and fragments.
    /// </summary>
    /// <remarks>
    /// Not a general-purpose hash: it seeds with the length in the high 16 bits,
    /// sums the body as little-endian 32-bit words, and folds any trailing 1-3
    /// bytes in big-endian order with a descending shift. The trailing-byte rule
    /// is the part that is easy to get wrong, and getting it wrong only shows up
    /// on payloads whose length is not a multiple of four.
    ///
    /// Matches ACE's <c>ACE.Common.Cryptography.Hash32</c>, which is the reference
    /// implementation for the servers this talks to.
    /// </remarks>
    public static class Hash32
    {
        public static uint Calculate(ReadOnlySpan<byte> data)
        {
            uint checksum = (uint)data.Length << 16;

            int i = 0;
            for (; i + 4 <= data.Length; i += 4)
                checksum += BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i));

            // Trailing bytes are folded in most-significant-first: the byte at
            // offset (length/4)*4 lands in bits 31..24, the next in 23..16, and so on.
            int shift = 3;
            while (i < data.Length)
                checksum += (uint)(data[i++] << (8 * shift--));

            return checksum;
        }
    }
}
