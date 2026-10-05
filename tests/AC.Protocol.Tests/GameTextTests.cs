using AC.Protocol;
using Xunit;

namespace AC.Protocol.Tests
{
    /// <summary>The game's strings are Windows-1252, whose curly quotes Latin-1 would turn into control characters.</summary>
    public class GameTextTests
    {
        [Fact]
        public void ACurlyQuoteIsReadAsOne()
        {
            // "Blackmoor's Favor" as the server writes it: a 16-bit length, the bytes with 0x92 for
            // the quote, then padding to a multiple of four.
            byte[] name = { (byte)'B', (byte)'l', (byte)'a', (byte)'c', (byte)'k', (byte)'m', (byte)'o', (byte)'o', (byte)'r', 0x92, (byte)'s' };
            byte[] field = new byte[2 + name.Length + 3];
            field[0] = (byte)name.Length;
            name.CopyTo(field, 2);

            SpanReader reader = new SpanReader(field);
            Assert.True(reader.TryReadString(out string value));
            Assert.Equal("Blackmoor’s", value);
            Assert.Equal(name, AcEncoding.Text.GetBytes(value));
        }

        [Fact]
        public void EveryByteRoundTrips()
        {
            byte[] all = new byte[256];
            for (int i = 0; i < all.Length; i++)
                all[i] = (byte)i;

            Assert.Equal(all, AcEncoding.Text.GetBytes(AcEncoding.Text.GetString(all)));
        }
    }
}
