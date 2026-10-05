using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using AC.Protocol;
using Xunit;

namespace AC.Protocol.Tests
{
    public class PacketHeaderTests
    {
        [Fact]
        public void HeaderRoundTrips()
        {
            PacketHeader written = new PacketHeader
            {
                Sequence = 0x11223344,
                Flags = PacketHeaderFlags.BlobFragments | PacketHeaderFlags.EncryptedChecksum,
                Checksum = 0xDEADBEEF,
                Id = 0x1234,
                Time = 0x5678,
                BodySize = 0x0140,
                Iteration = 0x0009,
            };

            byte[] buffer = new byte[PacketHeader.Size];
            written.Write(buffer);

            Assert.True(PacketHeader.TryParse(buffer, out PacketHeader read));

            Assert.Equal(written.Sequence, read.Sequence);
            Assert.Equal(written.Flags, read.Flags);
            Assert.Equal(written.Checksum, read.Checksum);
            Assert.Equal(written.Id, read.Id);
            Assert.Equal(written.Time, read.Time);
            Assert.Equal(written.BodySize, read.BodySize);
            Assert.Equal(written.Iteration, read.Iteration);
        }

        [Fact]
        public void HeaderIsWrittenLittleEndianInFieldOrder()
        {
            PacketHeader header = new PacketHeader
            {
                Sequence = 0x04030201,
                Flags = (PacketHeaderFlags)0x08070605,
                Checksum = 0x0C0B0A09,
                Id = 0x0E0D,
                Time = 0x100F,
                BodySize = 0x1211,
                Iteration = 0x1413,
            };

            byte[] buffer = new byte[PacketHeader.Size];
            header.Write(buffer);

            Assert.Equal(
                new byte[]
                {
                    0x01, 0x02, 0x03, 0x04,
                    0x05, 0x06, 0x07, 0x08,
                    0x09, 0x0A, 0x0B, 0x0C,
                    0x0D, 0x0E,
                    0x0F, 0x10,
                    0x11, 0x12,
                    0x13, 0x14,
                },
                buffer);
        }

        [Fact]
        public void TooShortForAHeaderIsRejected()
        {
            Assert.False(PacketHeader.TryParse(new byte[PacketHeader.Size - 1], out _));
        }

        [Fact]
        public void HeaderHashExcludesTheChecksumField()
        {
            // The checksum cannot cover itself, so it is replaced by a fixed
            // placeholder during hashing. Two headers differing only in checksum must
            // therefore hash identically - otherwise verification could never succeed.
            PacketHeader a = new PacketHeader { Sequence = 7, Checksum = 0 };
            PacketHeader b = new PacketHeader { Sequence = 7, Checksum = 0xFFFFFFFF };

            Assert.Equal(a.CalculateHash32(), b.CalculateHash32());
        }

        [Fact]
        public void HeaderHashDoesNotDisturbTheChecksumField()
        {
            PacketHeader header = new PacketHeader { Sequence = 7, Checksum = 0x12345678 };

            header.CalculateHash32();

            Assert.Equal(0x12345678u, header.Checksum);
        }

        [Fact]
        public void HeaderHashMatchesTheHashOfTheHeaderWithThePlaceholder()
        {
            PacketHeader header = new PacketHeader
            {
                Sequence = 42,
                Flags = PacketHeaderFlags.AckSequence,
                Checksum = 0x99999999,
                Id = 3,
                Time = 4,
                BodySize = 8,
                Iteration = 1,
            };

            PacketHeader stamped = header;
            stamped.Checksum = PacketHeader.ChecksumPlaceholder;

            byte[] buffer = new byte[PacketHeader.Size];
            stamped.Write(buffer);

            Assert.Equal(Hash32.Calculate(buffer), header.CalculateHash32());
        }
    }

    public class FragmentHeaderTests
    {
        [Fact]
        public void FragmentHeaderRoundTrips()
        {
            FragmentHeader written = new FragmentHeader
            {
                Sequence = 0xAABBCCDD,
                Id = 0x11223344,
                Count = 3,
                TotalSize = 100,
                Index = 1,
                Queue = 9,
            };

            byte[] buffer = new byte[FragmentHeader.Size];
            written.Write(buffer);

            Assert.True(FragmentHeader.TryParse(buffer, out FragmentHeader read));

            Assert.Equal(written.Sequence, read.Sequence);
            Assert.Equal(written.Id, read.Id);
            Assert.Equal(written.Count, read.Count);
            Assert.Equal(written.TotalSize, read.TotalSize);
            Assert.Equal(written.Index, read.Index);
            Assert.Equal(written.Queue, read.Queue);
            Assert.Equal(100 - FragmentHeader.Size, read.PayloadSize);
        }

        [Theory]
        // TotalSize below the header size would make PayloadSize negative.
        [InlineData(FragmentHeader.Size - 1, 1, 0)]
        // Above the protocol's per-fragment ceiling.
        [InlineData(FragmentHeader.MaxTotalSize + 1, 1, 0)]
        // A message of zero fragments cannot exist.
        [InlineData(64, 0, 0)]
        // An index outside the declared count.
        [InlineData(64, 2, 2)]
        [InlineData(64, 1, 5)]
        public void ImplausibleFragmentHeadersAreNotWellFormed(int totalSize, int count, int index)
        {
            FragmentHeader header = new FragmentHeader
            {
                TotalSize = (ushort)totalSize,
                Count = (ushort)count,
                Index = (ushort)index,
            };

            Assert.False(header.IsWellFormed);
        }

        [Fact]
        public void AnEmptyPayloadIsWellFormed()
        {
            // TotalSize == header size means a zero-length payload, which the format
            // permits and the assembler must handle.
            FragmentHeader header = new FragmentHeader
            {
                TotalSize = FragmentHeader.Size,
                Count = 1,
                Index = 0,
            };

            Assert.True(header.IsWellFormed);
            Assert.Equal(0, header.PayloadSize);
        }
    }

    public class AcPacketTests
    {
        private static byte[] BuildWithFragments(uint opcode, byte[] payload, uint sequence = 1)
        {
            IReadOnlyList<AcFragment> fragments = PacketWriter.Fragment(opcode, payload, sequence);

            PacketHeader header = new PacketHeader
            {
                Sequence = 5,
                Flags = PacketHeaderFlags.BlobFragments,
                Id = 1,
                Time = 2,
                Iteration = 1,
            };

            return PacketWriter.Build(header, ReadOnlySpan<byte>.Empty, fragments);
        }

        [Fact]
        public void APacketWithOneFragmentParsesBackToItsMessage()
        {
            byte[] payload = { 0xDE, 0xAD, 0xBE, 0xEF };
            byte[] datagram = BuildWithFragments(0xF7B0, payload);

            Assert.True(AcPacket.TryParse(datagram, out AcPacket packet));
            Assert.Single(packet.Fragments);
            Assert.Equal(0, packet.Optional.Length);

            MessageAssembler assembler = new MessageAssembler();
            List<AcMessage> messages = new List<AcMessage>(assembler.Accept(packet));

            Assert.Single(messages);
            Assert.Equal(0xF7B0u, messages[0].Opcode);
            Assert.Equal(payload, messages[0].Payload.ToArray());
        }

        [Fact]
        public void ABuiltPacketCarriesAValidPlainChecksum()
        {
            byte[] datagram = BuildWithFragments(0x1234, new byte[] { 1, 2, 3 });

            Assert.True(AcPacket.TryParse(datagram, out AcPacket packet));
            Assert.True(packet.HasValidPlainChecksum());
        }

        [Fact]
        public void FlippingAPayloadByteInvalidatesTheChecksum()
        {
            byte[] datagram = BuildWithFragments(0x1234, new byte[] { 1, 2, 3 });

            datagram[datagram.Length - 1] ^= 0xFF;

            Assert.True(AcPacket.TryParse(datagram, out AcPacket packet));
            Assert.False(packet.HasValidPlainChecksum());
        }

        [Fact]
        public void AnEncryptedChecksumIsReportedAsUnverifiedRatherThanValid()
        {
            // The plain checksum cannot be checked without the connection's ISAAC
            // stream, so claiming validity would be a lie either way.
            PacketHeader header = new PacketHeader
            {
                Sequence = 1,
                Flags = PacketHeaderFlags.EncryptedChecksum,
            };

            byte[] datagram = PacketWriter.Build(header, ReadOnlySpan<byte>.Empty);

            Assert.True(AcPacket.TryParse(datagram, out AcPacket packet));
            Assert.False(packet.HasValidPlainChecksum());
        }

        [Fact]
        public void ABodyShorterThanAdvertisedIsRejected()
        {
            byte[] datagram = new byte[PacketHeader.Size + 4];
            PacketHeader header = new PacketHeader { BodySize = 64 };
            header.Write(datagram);

            Assert.False(AcPacket.TryParse(datagram, out _));
        }

        [Fact]
        public void TrailingBytesBeyondTheDeclaredBodyAreIgnored()
        {
            // A body longer than advertised is not corruption; the extra bytes are
            // simply not part of this packet.
            byte[] datagram = BuildWithFragments(0x1234, new byte[] { 9, 9, 9, 9 });
            byte[] padded = new byte[datagram.Length + 7];
            datagram.CopyTo(padded, 0);

            Assert.True(AcPacket.TryParse(padded, out AcPacket packet));
            Assert.Single(packet.Fragments);
            Assert.True(packet.HasValidPlainChecksum());
        }

        [Fact]
        public void EmptyInputIsRejected()
        {
            Assert.False(AcPacket.TryParse(ReadOnlySpan<byte>.Empty, out _));
        }

        [Fact]
        public void AFragmentRegionThatEndsMidHeaderIsRejected()
        {
            // 8 body bytes claiming to be fragments cannot hold a 16-byte header.
            byte[] datagram = new byte[PacketHeader.Size + 8];
            PacketHeader header = new PacketHeader
            {
                Flags = PacketHeaderFlags.BlobFragments,
                BodySize = 8,
            };
            header.Write(datagram);

            Assert.False(AcPacket.TryParse(datagram, out _));
        }

        [Fact]
        public void AFragmentClaimingMorePayloadThanIsPresentIsRejected()
        {
            byte[] datagram = new byte[PacketHeader.Size + FragmentHeader.Size + 4];

            PacketHeader header = new PacketHeader
            {
                Flags = PacketHeaderFlags.BlobFragments,
                BodySize = (ushort)(FragmentHeader.Size + 4),
            };
            header.Write(datagram);

            // Declares 100 payload bytes; only 4 follow.
            new FragmentHeader
            {
                Sequence = 1,
                Count = 1,
                Index = 0,
                TotalSize = (ushort)(FragmentHeader.Size + 100),
            }.Write(datagram.AsSpan(PacketHeader.Size));

            Assert.False(AcPacket.TryParse(datagram, out _));
        }

        [Fact]
        public void AnOversizedFragmentIsRejected()
        {
            int payloadSize = FragmentHeader.MaxTotalSize;
            int bodySize = FragmentHeader.Size + payloadSize;
            byte[] datagram = new byte[PacketHeader.Size + bodySize];

            PacketHeader header = new PacketHeader
            {
                Flags = PacketHeaderFlags.BlobFragments,
                BodySize = (ushort)bodySize,
            };
            header.Write(datagram);

            new FragmentHeader
            {
                Sequence = 1,
                Count = 1,
                Index = 0,
                TotalSize = (ushort)bodySize, // over MaxTotalSize
            }.Write(datagram.AsSpan(PacketHeader.Size));

            Assert.False(AcPacket.TryParse(datagram, out _));
        }

        [Fact]
        public void WithoutTheFragmentsFlagTheBodyIsNotReadAsFragments()
        {
            // Bytes that would be nonsense as a fragment header must be left alone
            // when the flag says there are no fragments.
            byte[] datagram = new byte[PacketHeader.Size + 8];
            PacketHeader header = new PacketHeader { BodySize = 8 };
            header.Write(datagram);
            datagram.AsSpan(PacketHeader.Size).Fill(0xFF);

            Assert.True(AcPacket.TryParse(datagram, out AcPacket packet));
            Assert.Empty(packet.Fragments);
        }
    }

    public class OptionalHeaderTests
    {
        private static byte[] BuildWithOptional(PacketHeaderFlags flags, byte[] optional)
        {
            PacketHeader header = new PacketHeader { Sequence = 1, Flags = flags };
            return PacketWriter.Build(header, optional);
        }

        [Fact]
        public void AckSequenceIsRead()
        {
            byte[] optional = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(optional, 0x01020304);

            byte[] datagram = BuildWithOptional(PacketHeaderFlags.AckSequence, optional);

            Assert.True(AcPacket.TryParse(datagram, out AcPacket packet));
            Assert.Equal(0x01020304u, packet.Optional.AckSequence);
            Assert.Equal(4, packet.Optional.Length);
            Assert.True(packet.HasValidPlainChecksum());
        }

        [Fact]
        public void TimeSyncIsReadAsADouble()
        {
            byte[] optional = new byte[8];
            BinaryPrimitives.WriteDoubleLittleEndian(optional, 1234.5);

            byte[] datagram = BuildWithOptional(PacketHeaderFlags.TimeSync, optional);

            Assert.True(AcPacket.TryParse(datagram, out AcPacket packet));
            Assert.Equal(1234.5, packet.Optional.TimeSync);
        }

        [Fact]
        public void EchoRequestIsReadAsASingle()
        {
            byte[] optional = new byte[4];
            BinaryPrimitives.WriteSingleLittleEndian(optional, 7.25f);

            byte[] datagram = BuildWithOptional(PacketHeaderFlags.EchoRequest, optional);

            Assert.True(AcPacket.TryParse(datagram, out AcPacket packet));
            Assert.Equal(7.25f, packet.Optional.EchoRequestClientTime);
        }

        [Fact]
        public void FlowIsReadAsFourBytesThenTwo()
        {
            byte[] optional = new byte[6];
            BinaryPrimitives.WriteUInt32LittleEndian(optional, 4096);
            BinaryPrimitives.WriteUInt16LittleEndian(optional.AsSpan(4), 250);

            byte[] datagram = BuildWithOptional(PacketHeaderFlags.Flow, optional);

            Assert.True(AcPacket.TryParse(datagram, out AcPacket packet));
            Assert.Equal(4096u, packet.Optional.FlowBytes);
            Assert.Equal((ushort)250, packet.Optional.FlowInterval);
            Assert.Equal(6, packet.Optional.Length);
        }

        [Fact]
        public void RetransmitRequestsAreRead()
        {
            byte[] optional = new byte[4 + 12];
            BinaryPrimitives.WriteUInt32LittleEndian(optional, 3);
            BinaryPrimitives.WriteUInt32LittleEndian(optional.AsSpan(4), 10);
            BinaryPrimitives.WriteUInt32LittleEndian(optional.AsSpan(8), 11);
            BinaryPrimitives.WriteUInt32LittleEndian(optional.AsSpan(12), 12);

            byte[] datagram = BuildWithOptional(PacketHeaderFlags.RequestRetransmit, optional);

            Assert.True(AcPacket.TryParse(datagram, out AcPacket packet));
            Assert.Equal(new uint[] { 10, 11, 12 }, packet.Optional.RetransmitRequests);
        }

        [Fact]
        public void ARetransmitCountBeyondTheAvailableBytesIsRejected()
        {
            // The count is attacker-controlled and is used to size an array, so it has
            // to be validated against what is actually present before allocating.
            byte[] datagram = new byte[PacketHeader.Size + 8];
            PacketHeader header = new PacketHeader
            {
                Flags = PacketHeaderFlags.RequestRetransmit,
                BodySize = 8,
            };
            header.Write(datagram);
            BinaryPrimitives.WriteUInt32LittleEndian(datagram.AsSpan(PacketHeader.Size), 0xFFFFFFFF);

            Assert.False(AcPacket.TryParse(datagram, out _));
        }

        [Fact]
        public void MultipleOptionalFieldsAreReadInAscendingFlagOrder()
        {
            // AckSequence (0x4000) precedes TimeSync (0x1000000) on the wire. Reading
            // them in the wrong order would misparse both, and each would still look
            // plausible on its own.
            byte[] optional = new byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(optional, 77);
            BinaryPrimitives.WriteDoubleLittleEndian(optional.AsSpan(4), 99.5);

            byte[] datagram = BuildWithOptional(
                PacketHeaderFlags.AckSequence | PacketHeaderFlags.TimeSync,
                optional);

            Assert.True(AcPacket.TryParse(datagram, out AcPacket packet));
            Assert.Equal(77u, packet.Optional.AckSequence);
            Assert.Equal(99.5, packet.Optional.TimeSync);
            Assert.Equal(12, packet.Optional.Length);
        }

        [Fact]
        public void ATruncatedOptionalFieldIsRejected()
        {
            byte[] datagram = new byte[PacketHeader.Size + 2];
            PacketHeader header = new PacketHeader
            {
                Flags = PacketHeaderFlags.AckSequence, // needs 4 bytes, 2 present
                BodySize = 2,
            };
            header.Write(datagram);

            Assert.False(AcPacket.TryParse(datagram, out _));
        }

        [Fact]
        public void FragmentsFollowTheOptionalSection()
        {
            // Both an optional section and fragments: the fragment reader must start
            // after the optional bytes, not at the body's start.
            byte[] optional = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(optional, 4242);

            IReadOnlyList<AcFragment> fragments = PacketWriter.Fragment(0xABCD, new byte[] { 7, 7 }, 1);

            PacketHeader header = new PacketHeader
            {
                Sequence = 1,
                Flags = PacketHeaderFlags.AckSequence | PacketHeaderFlags.BlobFragments,
            };

            byte[] datagram = PacketWriter.Build(header, optional, fragments);

            Assert.True(AcPacket.TryParse(datagram, out AcPacket packet));
            Assert.Equal(4242u, packet.Optional.AckSequence);
            Assert.Single(packet.Fragments);
            Assert.True(packet.HasValidPlainChecksum());

            MessageAssembler assembler = new MessageAssembler();
            List<AcMessage> messages = new List<AcMessage>(assembler.Accept(packet));

            Assert.Single(messages);
            Assert.Equal(0xABCDu, messages[0].Opcode);
            Assert.Equal(new byte[] { 7, 7 }, messages[0].Payload.ToArray());
        }
    }
}
