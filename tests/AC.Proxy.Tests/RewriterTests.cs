using System;
using System.Collections.Generic;
using AC.Protocol;
using AC.Proxy;
using Xunit;

namespace AC.Proxy.Tests
{
    /// <summary>
    /// The rewriter is the one component that changes bytes on their way to the
    /// server, so the bar is that a rewritten packet is indistinguishable from one
    /// the client could have sent: same key, consecutive fragment numbers, inside the
    /// size limit.
    /// </summary>
    public class RewriterTests
    {
        private const uint GameAction = 0xF7B1;

        /// <summary>Builds a client packet the way a live client does: encrypted checksum.</summary>
        private static byte[] ClientPacket(uint packetSequence, uint key, params (uint sequence, int payloadSize)[] fragments)
        {
            List<AcFragment> built = new List<AcFragment>();

            foreach ((uint sequence, int payloadSize) in fragments)
            {
                byte[] payload = new byte[payloadSize];
                for (int i = 0; i < payload.Length; i++)
                    payload[i] = (byte)(sequence + i);

                built.Add(new AcFragment(
                    new FragmentHeader
                    {
                        Sequence = sequence,
                        Count = 1,
                        Index = 0,
                        TotalSize = (ushort)(FragmentHeader.Size + payload.Length),
                    },
                    payload));
            }

            PacketHeaderFlags flags = PacketHeaderFlags.EncryptedChecksum;
            if (built.Count > 0)
                flags |= PacketHeaderFlags.BlobFragments;

            return PacketWriter.BuildEncrypted(
                new PacketHeader { Sequence = packetSequence, Flags = flags },
                ReadOnlySpan<byte>.Empty,
                built,
                key);
        }

        /// <summary>What the server does: recover the key and check it is the expected one.</summary>
        private static uint KeyOf(byte[] datagram)
        {
            Assert.True(AcPacket.TryParse(datagram, out AcPacket packet));
            Assert.True(packet.TryRecoverEncryptionKey(out uint key));
            return key;
        }

        private static List<uint> FragmentSequences(byte[] datagram)
        {
            Assert.True(AcPacket.TryParse(datagram, out AcPacket packet));

            List<uint> sequences = new List<uint>();
            foreach (AcFragment fragment in packet.Fragments)
                sequences.Add(fragment.Header.Sequence);

            return sequences;
        }

        [Fact]
        public void AKeyStampedIntoAPacketComesBackOut()
        {
            // The whole approach rests on this: the key is recoverable from the packet
            // it produced, so a packet can be altered and re-stamped without ever
            // drawing from the connection's key stream.
            const uint key = 0xDEADBEEF;
            byte[] datagram = ClientPacket(10, key, (1, 8));

            Assert.Equal(key, KeyOf(datagram));
        }

        [Fact]
        public void APlainChecksumPacketYieldsNoKey()
        {
            byte[] datagram = PacketWriter.Build(
                new PacketHeader { Sequence = 1 },
                ReadOnlySpan<byte>.Empty);

            Assert.True(AcPacket.TryParse(datagram, out AcPacket packet));
            Assert.False(packet.TryRecoverEncryptionKey(out _));
        }

        [Fact]
        public void WithNothingQueuedThePacketIsPassedThroughUnchanged()
        {
            ClientStreamRewriter rewriter = new ClientStreamRewriter();
            byte[] datagram = ClientPacket(10, 0x1111, (1, 8));

            Assert.Same(datagram, rewriter.Rewrite(datagram));
            Assert.Equal(0, rewriter.InjectedMessages);
        }

        [Fact]
        public void AQueuedMessageRidesOutOnTheNextPacketKeepingItsKey()
        {
            const uint key = 0x0BADF00D;
            ClientStreamRewriter rewriter = new ClientStreamRewriter();
            rewriter.Enqueue(GameAction, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

            byte[] original = ClientPacket(10, key, (1, 8));
            byte[] rewritten = rewriter.Rewrite(original);

            Assert.NotSame(original, rewritten);
            Assert.Equal(key, KeyOf(rewritten));
            Assert.Equal(1, rewriter.InjectedMessages);
            Assert.Equal(0, rewriter.PendingMessages);

            // The client's fragment keeps its number; ours takes the next.
            Assert.Equal(new uint[] { 1, 2 }, FragmentSequences(rewritten));
        }

        [Fact]
        public void TheInjectedFragmentCarriesTheMessageIntact()
        {
            ClientStreamRewriter rewriter = new ClientStreamRewriter();
            byte[] payload = { 0xAA, 0xBB, 0xCC, 0xDD };
            rewriter.Enqueue(GameAction, payload);

            byte[] rewritten = rewriter.Rewrite(ClientPacket(10, 0x2222, (1, 8)));

            Assert.True(AcPacket.TryParse(rewritten, out AcPacket packet));

            MessageAssembler assembler = new MessageAssembler();
            List<AcMessage> messages = new List<AcMessage>(assembler.Accept(packet));

            AcMessage ours = messages.Find(m => m.Opcode == GameAction);
            Assert.NotNull(ours);
            Assert.Equal(payload, ours.Payload.ToArray());
        }

        [Fact]
        public void EveryLaterClientFragmentShiftsByTheNumberInjected()
        {
            // The server accepts a fragment only when it is exactly one past the last,
            // so inserting one has to renumber everything after it for good.
            ClientStreamRewriter rewriter = new ClientStreamRewriter();
            rewriter.Enqueue(GameAction, new byte[] { 1, 2, 3, 4 });

            byte[] first = rewriter.Rewrite(ClientPacket(10, 0x3333, (1, 8)));
            Assert.Equal(new uint[] { 1, 2 }, FragmentSequences(first));

            byte[] second = rewriter.Rewrite(ClientPacket(11, 0x4444, (2, 8)));
            Assert.Equal(new uint[] { 3 }, FragmentSequences(second));

            byte[] third = rewriter.Rewrite(ClientPacket(12, 0x5555, (3, 8), (4, 8)));
            Assert.Equal(new uint[] { 4, 5 }, FragmentSequences(third));
        }

        [Fact]
        public void TheServerSeesOneUnbrokenRunOfFragmentNumbers()
        {
            // The property that actually matters, asserted over a whole session with
            // injections interleaved at awkward moments.
            ClientStreamRewriter rewriter = new ClientStreamRewriter();
            List<uint> seen = new List<uint>();

            for (uint clientFragment = 1; clientFragment <= 20; clientFragment++)
            {
                if (clientFragment % 4 == 0)
                    rewriter.Enqueue(GameAction, new byte[] { 9, 9, 9, 9 });

                byte[] rewritten = rewriter.Rewrite(ClientPacket(clientFragment + 100, 0x6666, (clientFragment, 12)));
                seen.AddRange(FragmentSequences(rewritten));
            }

            Assert.Equal(25, seen.Count); // 20 from the client, 5 of ours

            for (int i = 0; i < seen.Count; i++)
                Assert.Equal((uint)(i + 1), seen[i]);
        }

        [Fact]
        public void AMessageCanGoOutOnAPacketThatCarriedNoFragments()
        {
            // Acks and echoes are frequent and fragment-free; they are the quickest
            // ride out, and the packet must gain the fragments flag to carry one.
            ClientStreamRewriter rewriter = new ClientStreamRewriter();
            rewriter.Enqueue(GameAction, new byte[] { 1, 2, 3, 4 });

            byte[] rewritten = rewriter.Rewrite(ClientPacket(10, 0x7777));

            Assert.True(AcPacket.TryParse(rewritten, out AcPacket packet));
            Assert.True(packet.Header.HasFlag(PacketHeaderFlags.BlobFragments));
            Assert.Single(packet.Fragments);
            Assert.Equal(1u, packet.Fragments[0].Header.Sequence);
        }

        [Fact]
        public void ARetransmittedFragmentKeepsTheNumberItWasFirstGiven()
        {
            // Recomputing it would put the server's run out of step and it would wait
            // forever for a number that is never coming.
            ClientStreamRewriter rewriter = new ClientStreamRewriter();
            rewriter.Enqueue(GameAction, new byte[] { 1, 2, 3, 4 });

            rewriter.Rewrite(ClientPacket(10, 0x8888, (1, 8)));
            byte[] second = rewriter.Rewrite(ClientPacket(11, 0x9999, (2, 8)));
            Assert.Equal(new uint[] { 3 }, FragmentSequences(second));

            // The client resends fragment 2 after a retransmit request.
            byte[] resent = rewriter.Rewrite(ClientPacket(11, 0xAAAA, (2, 8)));
            Assert.Equal(new uint[] { 3 }, FragmentSequences(resent));
        }

        [Fact]
        public void AMessageWaitsRatherThanOverfillThePacket()
        {
            ClientStreamRewriter rewriter = new ClientStreamRewriter();
            rewriter.Enqueue(GameAction, new byte[64]);

            // A packet already close to the limit leaves no room.
            byte[] full = ClientPacket(10, 0xBBBB, (1, 400), (2, 400), (3, 140));
            byte[] result = rewriter.Rewrite(full);

            Assert.Equal(1, rewriter.PendingMessages);
            Assert.Equal(0, rewriter.InjectedMessages);
            Assert.True(result.Length <= ClientStreamRewriter.MaxPacketSize);

            // The next, smaller packet has room.
            byte[] roomy = rewriter.Rewrite(ClientPacket(11, 0xCCCC, (4, 8)));
            Assert.Equal(0, rewriter.PendingMessages);
            Assert.Equal(1, rewriter.InjectedMessages);
            Assert.True(roomy.Length <= ClientStreamRewriter.MaxPacketSize);
        }

        [Fact]
        public void NoRewrittenPacketExceedsTheProtocolLimit()
        {
            ClientStreamRewriter rewriter = new ClientStreamRewriter();

            for (uint i = 1; i <= 10; i++)
            {
                rewriter.Enqueue(GameAction, new byte[32]);
                byte[] rewritten = rewriter.Rewrite(ClientPacket(i, 0xDDDD, (i, 300), (i + 100, 300)));
                Assert.True(rewritten.Length <= ClientStreamRewriter.MaxPacketSize, $"packet {i} was {rewritten.Length} bytes");
            }
        }

        [Fact]
        public void APacketWhoseKeyCannotBeRecoveredIsForwardedUntouched()
        {
            // Handshake packets are not encrypted. Relaying the session correctly
            // outranks getting a message of ours out.
            ClientStreamRewriter rewriter = new ClientStreamRewriter();
            rewriter.Enqueue(GameAction, new byte[] { 1, 2, 3, 4 });

            byte[] plain = PacketWriter.Build(
                new PacketHeader { Sequence = 1, Flags = PacketHeaderFlags.AckSequence },
                new byte[4]);

            Assert.Same(plain, rewriter.Rewrite(plain));
            Assert.Equal(1, rewriter.PendingMessages);
            Assert.Equal(1, rewriter.UnrewritablePackets);
        }

        [Fact]
        public void UnparseableBytesAreForwardedUntouched()
        {
            ClientStreamRewriter rewriter = new ClientStreamRewriter();
            rewriter.Enqueue(GameAction, new byte[] { 1, 2, 3, 4 });

            byte[] garbage = { 0xDE, 0xAD };

            Assert.Same(garbage, rewriter.Rewrite(garbage));
            Assert.Equal(1, rewriter.PendingMessages);
        }

        [Fact]
        public void SeveralQueuedMessagesShareOnePacketWhenTheyFit()
        {
            ClientStreamRewriter rewriter = new ClientStreamRewriter();
            rewriter.Enqueue(GameAction, new byte[] { 1, 1, 1, 1 });
            rewriter.Enqueue(GameAction, new byte[] { 2, 2, 2, 2 });
            rewriter.Enqueue(GameAction, new byte[] { 3, 3, 3, 3 });

            byte[] rewritten = rewriter.Rewrite(ClientPacket(10, 0xEEEE, (1, 8)));

            Assert.Equal(0, rewriter.PendingMessages);
            Assert.Equal(3, rewriter.InjectedMessages);
            Assert.Equal(new uint[] { 1, 2, 3, 4 }, FragmentSequences(rewritten));
        }

        [Fact]
        public void AReconnectRestartsTheNumberingInsteadOfInheritingTheDeadSession()
        {
            // The live failure, reproduced. A stale client kept transmitting with
            // fragment numbers in the thousands; the real client then reconnected and
            // began again at 1. Carrying the high-water mark over placed the injected
            // fragment ~4000 slots ahead, where the server buffered it forever, and
            // the offset it left behind stalled every fragment after it. The player
            // was logged in, the world updated, and nothing they did registered.
            ClientStreamRewriter rewriter = new ClientStreamRewriter();

            // The dying session, numbering high.
            for (uint sequence = 3824; sequence <= 3830; sequence++)
                rewriter.Rewrite(ClientPacket(sequence, 0x1234, (sequence, 48)));

            // The new session starts over. Its first packet triggers the reset, and
            // anything still queued from the dead session is abandoned with it.
            byte[] first = rewriter.Rewrite(ClientPacket(1, 0x5678, (1, 12)));
            Assert.Equal(1, rewriter.SessionResets);
            Assert.Equal(new uint[] { 1 }, FragmentSequences(first));

            // The action is queued once the new session identifies the player, which
            // is how it happens live.
            rewriter.Enqueue(GameAction, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 });

            List<uint> sequences = FragmentSequences(rewriter.Rewrite(ClientPacket(2, 0x9ABC, (2, 12))));

            // Continues the run at 2, and ours takes 3 - not 4130.
            Assert.Equal(new uint[] { 2, 3 }, sequences);
            Assert.True(sequences[1] < 100, $"injected fragment took sequence {sequences[1]}, inheriting the dead session");
        }

        [Fact]
        public void AHandshakePacketRestartsTheNumbering()
        {
            ClientStreamRewriter rewriter = new ClientStreamRewriter();

            for (uint sequence = 500; sequence <= 505; sequence++)
                rewriter.Rewrite(ClientPacket(sequence, 0x1111, (sequence, 16)));

            // Logging in again: the client announces itself before renumbering.
            byte[] login = PacketWriter.Build(
                new PacketHeader { Sequence = 1, Flags = PacketHeaderFlags.LoginRequest },
                new byte[] { 1, 2, 3, 4 });

            rewriter.Rewrite(login);
            Assert.Equal(1, rewriter.SessionResets);

            rewriter.Enqueue(GameAction, new byte[] { 1, 2, 3, 4 });
            byte[] rewritten = rewriter.Rewrite(ClientPacket(2, 0x2222, (1, 12)));

            Assert.Equal(new uint[] { 1, 2 }, FragmentSequences(rewritten));
        }

        [Fact]
        public void AResetAbandonsQueuedMessagesRatherThanSendingThemIntoANewWorld()
        {
            // A queued action names objects from the old session; its ids mean nothing
            // in the new one even if the numbering were right.
            ClientStreamRewriter rewriter = new ClientStreamRewriter();

            for (uint sequence = 900; sequence <= 905; sequence++)
                rewriter.Rewrite(ClientPacket(sequence, 0x3333, (sequence, 16)));

            rewriter.Enqueue(GameAction, new byte[] { 1, 2, 3, 4 });
            Assert.Equal(1, rewriter.PendingMessages);

            rewriter.Rewrite(ClientPacket(1, 0x4444, (1, 12)));

            Assert.Equal(1, rewriter.SessionResets);
            Assert.Equal(0, rewriter.PendingMessages);
            Assert.Equal(0, rewriter.InjectedMessages);
        }

        [Fact]
        public void ARetransmissionIsNotMistakenForAReconnect()
        {
            // Stepping back a few numbers is ordinary; only a jump to the beginning is
            // a new session. Resetting on a retransmit would corrupt a healthy stream.
            ClientStreamRewriter rewriter = new ClientStreamRewriter();
            rewriter.Enqueue(GameAction, new byte[] { 1, 2, 3, 4 });

            rewriter.Rewrite(ClientPacket(10, 0x5555, (20, 12)));
            rewriter.Rewrite(ClientPacket(11, 0x6666, (21, 12)));

            // The client resends an earlier fragment.
            rewriter.Rewrite(ClientPacket(11, 0x7777, (19, 12)));

            Assert.Equal(0, rewriter.SessionResets);
        }

        [Fact]
        public void TheServerSeesAnUnbrokenRunAcrossAReconnect()
        {
            // The end-to-end property, now spanning the boundary that broke it live.
            ClientStreamRewriter rewriter = new ClientStreamRewriter();
            List<uint> firstSession = new List<uint>();

            for (uint sequence = 1; sequence <= 6; sequence++)
            {
                if (sequence == 3)
                    rewriter.Enqueue(GameAction, new byte[] { 7, 7, 7, 7 });

                firstSession.AddRange(FragmentSequences(rewriter.Rewrite(ClientPacket(sequence, 0x8888, (sequence, 16)))));
            }

            for (int i = 0; i < firstSession.Count; i++)
                Assert.Equal((uint)(i + 1), firstSession[i]);

            // Reconnect. A real one always announces itself first, which is what the
            // reset keys on; the backwards jump is only a fallback for a session too
            // short to have travelled far.
            rewriter.Rewrite(PacketWriter.Build(
                new PacketHeader { Sequence = 1, Flags = PacketHeaderFlags.LoginRequest },
                new byte[] { 1, 2, 3, 4 }));

            List<uint> secondSession = new List<uint>();

            for (uint sequence = 1; sequence <= 6; sequence++)
            {
                if (sequence == 2)
                    rewriter.Enqueue(GameAction, new byte[] { 8, 8, 8, 8 });

                secondSession.AddRange(FragmentSequences(rewriter.Rewrite(ClientPacket(sequence + 500, 0x9999, (sequence, 16)))));
            }

            Assert.Equal(1, rewriter.SessionResets);

            for (int i = 0; i < secondSession.Count; i++)
                Assert.Equal((uint)(i + 1), secondSession[i]);
        }

        [Fact]
        public void TheOptionalHeaderSectionSurvivesARewrite()
        {
            // Acks live in the optional section; losing one would make the server
            // retransmit needlessly.
            ClientStreamRewriter rewriter = new ClientStreamRewriter();
            rewriter.Enqueue(GameAction, new byte[] { 1, 2, 3, 4 });

            byte[] optional = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(optional, 4242);

            byte[] original = PacketWriter.BuildEncrypted(
                new PacketHeader
                {
                    Sequence = 10,
                    Flags = PacketHeaderFlags.EncryptedChecksum | PacketHeaderFlags.AckSequence,
                },
                optional,
                Array.Empty<AcFragment>(),
                0xFACE);

            byte[] rewritten = rewriter.Rewrite(original);

            Assert.True(AcPacket.TryParse(rewritten, out AcPacket packet));
            Assert.Equal(4242u, packet.Optional.AckSequence);
            Assert.Equal(0xFACEu, KeyOf(rewritten));
            Assert.Single(packet.Fragments);
        }
    }
}
