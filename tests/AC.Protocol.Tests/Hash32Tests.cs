using System;
using AC.Protocol;
using Xunit;

namespace AC.Protocol.Tests
{
    /// <summary>
    /// Vectors small enough to verify by arithmetic rather than by re-running the same
    /// algorithm. The trailing-byte rule is the part of this checksum that is easy to
    /// get subtly wrong - it folds the last 1-3 bytes in most-significant-first, the
    /// opposite of the little-endian word reads before it - so it is pinned per length.
    /// </summary>
    public class Hash32Tests
    {
        [Fact]
        public void EmptyInputHashesToZero()
        {
            // length 0: seed is 0 << 16, no words, no trailing bytes.
            Assert.Equal(0u, Hash32.Calculate(ReadOnlySpan<byte>.Empty));
        }

        [Fact]
        public void OneWordIsSeedPlusLittleEndianWord()
        {
            // length 4 -> 0x00040000, plus LE(01 02 03 04) = 0x04030201.
            Assert.Equal(0x04070201u, Hash32.Calculate(new byte[] { 0x01, 0x02, 0x03, 0x04 }));
        }

        [Fact]
        public void OneTrailingByteLandsInTheHighOctet()
        {
            // length 1 -> 0x00010000, plus 0xAA << 24.
            Assert.Equal(0xAA010000u, Hash32.Calculate(new byte[] { 0xAA }));
        }

        [Fact]
        public void TwoTrailingBytesDescendByOneOctet()
        {
            // length 2 -> 0x00020000, plus 0xAA << 24, plus 0xBB << 16.
            Assert.Equal(0xAABD0000u, Hash32.Calculate(new byte[] { 0xAA, 0xBB }));
        }

        [Fact]
        public void ThreeTrailingBytesFillDownToTheThirdOctet()
        {
            // length 3 -> 0x00030000, plus 0xAA << 24, 0xBB << 16, 0xCC << 8.
            Assert.Equal(0xAABECC00u, Hash32.Calculate(new byte[] { 0xAA, 0xBB, 0xCC }));
        }

        [Fact]
        public void TrailingByteAfterAFullWordRestartsTheShiftAtThree()
        {
            // length 5 -> 0x00050000, plus LE(01 02 03 04) = 0x04030201,
            // plus 0x05 << 24. The shift restarts at 3 for the remainder; it is not
            // continued from where the word reads left off.
            Assert.Equal(0x09080201u, Hash32.Calculate(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 }));
        }

        [Fact]
        public void SumWrapsRatherThanOverflowing()
        {
            // Four words of 0xFFFFFFFF sum to 0x03FFFFFFFC before the length seed,
            // which must wrap to 0xFFFFFFFC rather than throw.
            byte[] data = new byte[16];
            data.AsSpan().Fill(0xFF);

            uint expected = unchecked((uint)(16 << 16) + 0xFFFFFFFFu + 0xFFFFFFFFu + 0xFFFFFFFFu + 0xFFFFFFFFu);

            Assert.Equal(expected, Hash32.Calculate(data));
        }

        [Fact]
        public void LengthIsPartOfTheHash()
        {
            // Two inputs with identical word sums but different lengths must differ,
            // which is the point of seeding with the length.
            uint fourZeroes = Hash32.Calculate(new byte[] { 0, 0, 0, 0 });
            uint eightZeroes = Hash32.Calculate(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 });

            Assert.NotEqual(fourZeroes, eightZeroes);
            Assert.Equal(0x00040000u, fourZeroes);
            Assert.Equal(0x00080000u, eightZeroes);
        }
    }
}
