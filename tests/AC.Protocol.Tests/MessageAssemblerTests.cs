using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using AC.Protocol;
using Xunit;

namespace AC.Protocol.Tests
{
    /// <summary>
    /// Reassembly runs on UDP, so reordering, duplication and loss are the normal case
    /// rather than the edge case. Each of those gets a test.
    /// </summary>
    public class MessageAssemblerTests
    {
        private static AcFragment Fragment(uint sequence, int index, int count, byte[] payload)
            => new AcFragment(
                new FragmentHeader
                {
                    Sequence = sequence,
                    Count = (ushort)count,
                    Index = (ushort)index,
                    TotalSize = (ushort)(FragmentHeader.Size + payload.Length),
                },
                payload);

        /// <summary>Splits a message body across <paramref name="count"/> fragments.</summary>
        private static List<AcFragment> Split(uint sequence, uint opcode, byte[] payload, int count)
        {
            byte[] body = new byte[4 + payload.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(body, opcode);
            payload.CopyTo(body, 4);

            int per = (body.Length + count - 1) / count;
            List<AcFragment> fragments = new List<AcFragment>(count);

            for (int i = 0; i < count; i++)
            {
                int start = i * per;
                int length = Math.Min(per, Math.Max(0, body.Length - start));
                byte[] slice = new byte[length];
                Array.Copy(body, start, slice, 0, length);
                fragments.Add(Fragment(sequence, i, count, slice));
            }

            return fragments;
        }

        [Fact]
        public void ASingleFragmentMessageIsEmittedImmediately()
        {
            MessageAssembler assembler = new MessageAssembler();

            byte[] body = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(body, 0xF7B1);
            body[4] = 1;
            body[5] = 2;
            body[6] = 3;
            body[7] = 4;

            Assert.True(assembler.TryAccept(Fragment(100, 0, 1, body), out AcMessage message));

            Assert.Equal(0xF7B1u, message.Opcode);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, message.Payload.ToArray());
            Assert.Equal(100u, message.FragmentSequence);
            Assert.Equal(0, assembler.PendingMessages);
        }

        [Fact]
        public void FragmentsArrivingInOrderReassemble()
        {
            MessageAssembler assembler = new MessageAssembler();
            byte[] payload = new byte[300];
            for (int i = 0; i < payload.Length; i++)
                payload[i] = (byte)(i % 251);

            List<AcFragment> fragments = Split(7, 0x1111, payload, 3);

            Assert.False(assembler.TryAccept(fragments[0], out _));
            Assert.False(assembler.TryAccept(fragments[1], out _));
            Assert.Equal(1, assembler.PendingMessages);

            Assert.True(assembler.TryAccept(fragments[2], out AcMessage message));

            Assert.Equal(0x1111u, message.Opcode);
            Assert.Equal(payload, message.Payload.ToArray());
            Assert.Equal(0, assembler.PendingMessages);
        }

        [Fact]
        public void FragmentsArrivingOutOfOrderReassembleInIndexOrder()
        {
            MessageAssembler assembler = new MessageAssembler();
            byte[] payload = new byte[300];
            for (int i = 0; i < payload.Length; i++)
                payload[i] = (byte)(i % 251);

            List<AcFragment> fragments = Split(7, 0x2222, payload, 3);

            // Deliberately reversed: the result must be ordered by index, not arrival.
            Assert.False(assembler.TryAccept(fragments[2], out _));
            Assert.False(assembler.TryAccept(fragments[1], out _));
            Assert.True(assembler.TryAccept(fragments[0], out AcMessage message));

            Assert.Equal(0x2222u, message.Opcode);
            Assert.Equal(payload, message.Payload.ToArray());
        }

        [Fact]
        public void ADuplicateFragmentIsIgnoredAndDoesNotCompleteTheMessage()
        {
            MessageAssembler assembler = new MessageAssembler();
            List<AcFragment> fragments = Split(9, 0x3333, new byte[200], 3);

            Assert.False(assembler.TryAccept(fragments[0], out _));
            Assert.False(assembler.TryAccept(fragments[0], out _));

            Assert.Equal(1, assembler.DuplicateFragments);
            Assert.Equal(1, assembler.PendingMessages);

            // Still genuinely incomplete: the duplicate must not have counted.
            Assert.False(assembler.TryAccept(fragments[1], out _));
            Assert.True(assembler.TryAccept(fragments[2], out _));
        }

        [Fact]
        public void AnEmptyPayloadFragmentCountsAsPresent()
        {
            // A zero-length payload is legal, and must not read as an absent fragment -
            // otherwise the message never completes, or completes early.
            MessageAssembler assembler = new MessageAssembler();

            byte[] body = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(body, 0x4444);

            Assert.False(assembler.TryAccept(Fragment(11, 0, 2, body), out _));

            // The empty second fragment completes the message.
            Assert.True(assembler.TryAccept(Fragment(11, 1, 2, Array.Empty<byte>()), out AcMessage message));

            Assert.Equal(0x4444u, message.Opcode);
            Assert.Empty(message.Payload.ToArray());
            Assert.Equal(0, assembler.PendingMessages);
            Assert.Equal(0, assembler.DuplicateFragments);
        }

        [Fact]
        public void ConcurrentMessagesOnDifferentSequencesDoNotInterfere()
        {
            MessageAssembler assembler = new MessageAssembler();

            List<AcFragment> first = Split(1, 0xAAAA, new byte[100], 2);
            List<AcFragment> second = Split(2, 0xBBBB, new byte[100], 2);

            Assert.False(assembler.TryAccept(first[0], out _));
            Assert.False(assembler.TryAccept(second[0], out _));
            Assert.Equal(2, assembler.PendingMessages);

            Assert.True(assembler.TryAccept(second[1], out AcMessage secondMessage));
            Assert.Equal(0xBBBBu, secondMessage.Opcode);

            Assert.True(assembler.TryAccept(first[1], out AcMessage firstMessage));
            Assert.Equal(0xAAAAu, firstMessage.Opcode);
        }

        [Fact]
        public void AFragmentCountThatContradictsTheBufferRestartsTheMessage()
        {
            MessageAssembler assembler = new MessageAssembler();

            Assert.False(assembler.TryAccept(Fragment(5, 0, 3, new byte[] { 1, 2, 3, 4 }), out _));

            // Same sequence, different total count: the buffered state is untrustworthy.
            Assert.False(assembler.TryAccept(Fragment(5, 0, 2, new byte[] { 1, 2, 3, 4 }), out _));
            Assert.Equal(1, assembler.AbandonedMessages);

            Assert.True(assembler.TryAccept(Fragment(5, 1, 2, new byte[] { 5, 6 }), out AcMessage message));
            Assert.Equal(new byte[] { 5, 6 }, message.Payload.ToArray());
        }

        [Fact]
        public void AMalformedFragmentIsRejectedRatherThanBuffered()
        {
            MessageAssembler assembler = new MessageAssembler();

            AcFragment bad = new AcFragment(
                new FragmentHeader { Sequence = 1, Count = 0, Index = 0, TotalSize = 20 },
                new byte[4]);

            Assert.False(assembler.TryAccept(bad, out _));
            Assert.Equal(1, assembler.RejectedFragments);
            Assert.Equal(0, assembler.PendingMessages);
        }

        [Fact]
        public void AFragmentWhosePayloadContradictsItsHeaderIsRejected()
        {
            MessageAssembler assembler = new MessageAssembler();

            // Header says 100 payload bytes; 4 are attached.
            AcFragment bad = new AcFragment(
                new FragmentHeader
                {
                    Sequence = 1,
                    Count = 1,
                    Index = 0,
                    TotalSize = (ushort)(FragmentHeader.Size + 100),
                },
                new byte[4]);

            Assert.False(assembler.TryAccept(bad, out _));
            Assert.Equal(1, assembler.RejectedFragments);
        }

        [Fact]
        public void AMessageShorterThanAnOpcodeIsNotEmitted()
        {
            MessageAssembler assembler = new MessageAssembler();

            Assert.False(assembler.TryAccept(Fragment(1, 0, 1, new byte[] { 1, 2, 3 }), out AcMessage message));
            Assert.Null(message);
        }

        [Fact]
        public void PartialMessagesAreBoundedAndTheOldestIsDropped()
        {
            // Without a bound, a peer that starts messages and never finishes them
            // would grow this dictionary without limit.
            MessageAssembler assembler = new MessageAssembler();

            for (uint sequence = 1; sequence <= MessageAssembler.MaxPartialMessages; sequence++)
                assembler.TryAccept(Fragment(sequence, 0, 2, new byte[] { 1, 2, 3, 4 }), out _);

            Assert.Equal(MessageAssembler.MaxPartialMessages, assembler.PendingMessages);
            Assert.Equal(0, assembler.AbandonedMessages);

            assembler.TryAccept(Fragment(9999, 0, 2, new byte[] { 1, 2, 3, 4 }), out _);

            Assert.Equal(MessageAssembler.MaxPartialMessages, assembler.PendingMessages);
            Assert.Equal(1, assembler.AbandonedMessages);

            // Sequence 1 was the oldest, so its second fragment should now start afresh
            // rather than complete a message.
            Assert.False(assembler.TryAccept(Fragment(1, 1, 2, new byte[] { 5, 6 }), out _));
        }

        [Fact]
        public void ResetDiscardsPartialMessages()
        {
            MessageAssembler assembler = new MessageAssembler();

            Assert.False(assembler.TryAccept(Fragment(1, 0, 2, new byte[] { 1, 2, 3, 4 }), out _));
            Assert.Equal(1, assembler.PendingMessages);

            assembler.Reset();

            Assert.Equal(0, assembler.PendingMessages);
            Assert.Equal(1, assembler.AbandonedMessages);
        }

        [Fact]
        public void AcceptOnAPacketWithNoFragmentsYieldsNothing()
        {
            MessageAssembler assembler = new MessageAssembler();

            byte[] datagram = PacketWriter.Build(
                new PacketHeader { Sequence = 1, Flags = PacketHeaderFlags.AckSequence },
                new byte[4]);

            Assert.True(AcPacket.TryParse(datagram, out AcPacket packet));
            Assert.Empty(assembler.Accept(packet));
        }

        [Fact]
        public void AMessageLargerThanOneFragmentRoundTripsThroughTheWriter()
        {
            // The writer's split and the assembler's join must be inverses. A payload
            // over the per-fragment limit is the case that exercises both.
            byte[] payload = new byte[1200];
            for (int i = 0; i < payload.Length; i++)
                payload[i] = (byte)(i * 7 % 256);

            IReadOnlyList<AcFragment> fragments = PacketWriter.Fragment(0xC0DE, payload, 42);

            Assert.True(fragments.Count > 1);

            MessageAssembler assembler = new MessageAssembler();
            AcMessage message = null;

            foreach (AcFragment fragment in fragments)
            {
                if (assembler.TryAccept(fragment, out AcMessage completed))
                    message = completed;
            }

            Assert.NotNull(message);
            Assert.Equal(0xC0DEu, message.Opcode);
            Assert.Equal(payload, message.Payload.ToArray());
        }

        [Fact]
        public void EveryFragmentFromTheWriterFitsTheProtocolLimit()
        {
            IReadOnlyList<AcFragment> fragments = PacketWriter.Fragment(1, new byte[5000], 1);

            foreach (AcFragment fragment in fragments)
            {
                Assert.True(fragment.Header.IsWellFormed);
                Assert.True(fragment.Header.TotalSize <= FragmentHeader.MaxTotalSize);
                Assert.Equal(fragment.Header.PayloadSize, fragment.Payload.Length);
            }
        }
    }
}
