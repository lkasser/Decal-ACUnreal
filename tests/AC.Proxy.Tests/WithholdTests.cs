using System;
using System.Collections.Generic;
using AC.Protocol;
using AC.Proxy;
using Xunit;

namespace AC.Proxy.Tests
{
    /// <summary>
    /// Taking one of the client's messages out of the stream - a line the player typed for a
    /// plugin, which the server must never see. The bar is the one injection is held to: what
    /// the server receives is indistinguishable from a session in which the client never sent
    /// it, with the same keys, the same packet numbers and one unbroken run of fragments.
    /// </summary>
    public class WithholdTests
    {
        private const uint GameAction = 0xF7B1;

        /// <summary>A game action as the client sends it: opcode, sequence, type, then fields.</summary>
        private static byte[] Action(uint type, byte marker)
        {
            byte[] message = new byte[16];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(message, GameAction);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(4), 1);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(8), type);
            message[12] = marker;
            return message;
        }

        /// <summary>A client packet carrying whole one-fragment messages, encrypted as a live one is.</summary>
        private static byte[] ClientPacket(uint packetSequence, uint key, params (uint Sequence, byte[] Message)[] fragments)
        {
            List<AcFragment> built = new List<AcFragment>();
            foreach ((uint sequence, byte[] message) in fragments)
            {
                built.Add(new AcFragment(
                    new FragmentHeader
                    {
                        Sequence = sequence,
                        Count = 1,
                        Index = 0,
                        TotalSize = (ushort)(FragmentHeader.Size + message.Length),
                    },
                    message));
            }

            PacketHeaderFlags flags = PacketHeaderFlags.EncryptedChecksum;
            if (built.Count > 0)
                flags |= PacketHeaderFlags.BlobFragments;

            return PacketWriter.BuildEncrypted(new PacketHeader { Sequence = packetSequence, Flags = flags }, ReadOnlySpan<byte>.Empty, built, key);
        }

        private static AcPacket Parse(byte[] datagram)
        {
            Assert.True(AcPacket.TryParse(datagram, out AcPacket packet));
            return packet;
        }

        private static List<uint> Sequences(byte[] datagram)
        {
            List<uint> sequences = new List<uint>();
            foreach (AcFragment fragment in Parse(datagram).Fragments)
                sequences.Add(fragment.Header.Sequence);
            return sequences;
        }

        private static List<byte> Markers(byte[] datagram)
        {
            List<byte> markers = new List<byte>();
            foreach (AcFragment fragment in Parse(datagram).Fragments)
                markers.Add(fragment.Payload.Span[12]);
            return markers;
        }

        /// <summary>A rewriter that takes out every Talk (0x0015), recording what it took.</summary>
        private static ClientStreamRewriter TakingOutTalk(List<byte[]> taken)
        {
            ClientStreamRewriter rewriter = new ClientStreamRewriter
            {
                Withhold = (opcode, payload) => opcode == GameAction
                    && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(payload.Span.Slice(4)) == 0x0015,
            };
            rewriter.Withheld += (opcode, payload) => taken.Add(payload.ToArray());
            return rewriter;
        }

        [Fact]
        public void APickedMessageIsTakenOutAndEveryLaterFragmentMovesDownOne()
        {
            List<byte[]> taken = new List<byte[]>();
            ClientStreamRewriter rewriter = TakingOutTalk(taken);

            byte[] first = rewriter.Rewrite(ClientPacket(10, 0x1111, (1, Action(0xF753, 1)), (2, Action(0x0015, 2)), (3, Action(0xF753, 3))));

            // Same key, same packet number: the server sees a packet it expected.
            AcPacket packet = Parse(first);
            Assert.True(packet.TryRecoverEncryptionKey(out uint key));
            Assert.Equal(0x1111u, key);
            Assert.Equal(10u, packet.Header.Sequence);

            Assert.Equal(new uint[] { 1, 2 }, Sequences(first));
            Assert.Equal(new byte[] { 1, 3 }, Markers(first));
            Assert.Equal(1, rewriter.WithheldMessages);

            // Announced once, as the bytes after the opcode.
            Assert.Single(taken);
            Assert.Equal(2, taken[0][8]);

            byte[] second = rewriter.Rewrite(ClientPacket(11, 0x2222, (4, Action(0xF753, 4))));
            Assert.Equal(new uint[] { 3 }, Sequences(second));
        }

        [Fact]
        public void ARetransmissionOfATakenOutMessageIsTakenOutAgainAndNotAnnouncedTwice()
        {
            List<byte[]> taken = new List<byte[]>();
            ClientStreamRewriter rewriter = TakingOutTalk(taken);

            rewriter.Rewrite(ClientPacket(10, 0x1111, (1, Action(0x0015, 1)), (2, Action(0xF753, 2))));
            byte[] resent = rewriter.Rewrite(ClientPacket(10, 0x1111, (1, Action(0x0015, 1)), (2, Action(0xF753, 2))));

            // The kept fragment keeps the number it was first given.
            Assert.Equal(new uint[] { 1 }, Sequences(resent));
            Assert.Equal(new byte[] { 2 }, Markers(resent));
            Assert.Single(taken);
            Assert.Equal(1, rewriter.WithheldMessages);

            byte[] next = rewriter.Rewrite(ClientPacket(11, 0x3333, (3, Action(0xF753, 3))));
            Assert.Equal(new uint[] { 2 }, Sequences(next));
        }

        [Fact]
        public void APacketLeftWithNoFragmentsStillGoesAndSaysItHasNone()
        {
            // The server acknowledges packets by number; one that never arrived would be
            // asked for again for ever. So the packet goes, empty.
            ClientStreamRewriter rewriter = TakingOutTalk(new List<byte[]>());

            byte[] rewritten = rewriter.Rewrite(ClientPacket(10, 0x4444, (1, Action(0x0015, 1))));

            AcPacket packet = Parse(rewritten);
            Assert.Empty(packet.Fragments);
            Assert.False(packet.Header.HasFlag(PacketHeaderFlags.BlobFragments));
            Assert.Equal(10u, packet.Header.Sequence);
            Assert.True(packet.TryRecoverEncryptionKey(out uint key));
            Assert.Equal(0x4444u, key);
        }

        [Fact]
        public void TheServerSeesOneUnbrokenRunWithMessagesTakenOutAndInjected()
        {
            ClientStreamRewriter rewriter = TakingOutTalk(new List<byte[]>());
            List<uint> seen = new List<uint>();

            for (uint fragment = 1; fragment <= 24; fragment++)
            {
                if (fragment % 5 == 0)
                    rewriter.Enqueue(GameAction, new byte[] { 9, 9, 9, 9, 9, 9, 9, 9 });

                uint type = fragment % 3 == 0 ? 0x0015u : 0xF753u;
                seen.AddRange(Sequences(rewriter.Rewrite(ClientPacket(fragment + 100, 0x5555, (fragment, Action(type, (byte)fragment))))));
            }

            // 24 of the client's, 8 of them talk taken out, and 4 of ours put in.
            Assert.Equal(24 - 8 + 4, seen.Count);
            for (int i = 0; i < seen.Count; i++)
                Assert.Equal((uint)(i + 1), seen[i]);
        }

        [Fact]
        public void APacketThatCannotBeReStampedGoesAsItIsAndNothingIsAnnounced()
        {
            List<byte[]> taken = new List<byte[]>();
            ClientStreamRewriter rewriter = TakingOutTalk(taken);

            byte[] message = Action(0x0015, 1);
            AcFragment fragment = new AcFragment(
                new FragmentHeader { Sequence = 1, Count = 1, Index = 0, TotalSize = (ushort)(FragmentHeader.Size + message.Length) },
                message);
            byte[] plain = PacketWriter.Build(new PacketHeader { Sequence = 1, Flags = PacketHeaderFlags.BlobFragments }, ReadOnlySpan<byte>.Empty, new[] { fragment });

            Assert.Same(plain, rewriter.Rewrite(plain));
            Assert.Empty(taken);
            Assert.Equal(0, rewriter.WithheldMessages);
        }

        [Fact]
        public void APredicateThatFailsKeepsTheMessage()
        {
            ClientStreamRewriter rewriter = new ClientStreamRewriter { Withhold = (_, _) => throw new InvalidOperationException("broken") };
            byte[] datagram = ClientPacket(10, 0x7777, (1, Action(0x0015, 1)));

            Assert.Same(datagram, rewriter.Rewrite(datagram));
            Assert.Equal(0, rewriter.WithheldMessages);
        }

        /// <summary>
        /// A message that went through once - the test said no, or was not set yet - is on the
        /// server already. Taking its retransmission out would move every later number onto one
        /// the server has, so a retransmission is never offered.
        /// </summary>
        [Fact]
        public void ARetransmissionOfAMessageThatWentThroughIsNotTakenOut()
        {
            bool takeOut = false;
            ClientStreamRewriter rewriter = new ClientStreamRewriter { Withhold = (_, _) => takeOut };

            rewriter.Rewrite(ClientPacket(10, 0x1111, (1, Action(0x0015, 1))));
            rewriter.Rewrite(ClientPacket(11, 0x2222, (2, Action(0xF753, 2))));

            takeOut = true;
            byte[] resent = rewriter.Rewrite(ClientPacket(10, 0x1111, (1, Action(0x0015, 1))));

            Assert.Equal(new uint[] { 1 }, Sequences(resent));
            Assert.Equal(0, rewriter.WithheldMessages);
        }

        /// <summary>
        /// One message put in and one taken out bring the offset back to nothing, but fragments
        /// sent in between still carry their new numbers, and a retransmission must keep them.
        /// Fragments sent before anything was renumbered keep their own.
        /// </summary>
        [Fact]
        public void RetransmissionsKeepTheirNumbersWhenTheOffsetComesBackToZero()
        {
            ClientStreamRewriter rewriter = TakingOutTalk(new List<byte[]>());

            rewriter.Rewrite(ClientPacket(10, 0x1111, (1, Action(0xF753, 1))));
            rewriter.Enqueue(GameAction, new byte[] { 9, 9, 9, 9, 9, 9, 9, 9 });
            Assert.Equal(new uint[] { 2, 3 }, Sequences(rewriter.Rewrite(ClientPacket(11, 0x2222, (2, Action(0xF753, 2))))));
            Assert.Equal(new uint[] { 4 }, Sequences(rewriter.Rewrite(ClientPacket(12, 0x3333, (3, Action(0xF753, 3))))));
            Assert.Empty(Sequences(rewriter.Rewrite(ClientPacket(13, 0x4444, (4, Action(0x0015, 4))))));
            Assert.Equal(new uint[] { 5 }, Sequences(rewriter.Rewrite(ClientPacket(14, 0x5555, (5, Action(0xF753, 5))))));

            // The client resends 3, first sent as 4, and 1, sent before any change as 1.
            Assert.Equal(new uint[] { 4 }, Sequences(rewriter.Rewrite(ClientPacket(12, 0x3333, (3, Action(0xF753, 3))))));
            Assert.Equal(new uint[] { 1 }, Sequences(rewriter.Rewrite(ClientPacket(10, 0x1111, (1, Action(0xF753, 1))))));
        }

        [Fact]
        public void AReconnectForgetsWhatWasTakenOut()
        {
            ClientStreamRewriter rewriter = TakingOutTalk(new List<byte[]>());

            for (uint sequence = 200; sequence <= 205; sequence++)
                rewriter.Rewrite(ClientPacket(sequence, 0x1234, (sequence, Action(sequence == 202 ? 0x0015u : 0xF753u, 1))));

            // A new session numbers from 1 again: nothing is renumbered and nothing old is taken out.
            byte[] first = rewriter.Rewrite(ClientPacket(1, 0x5678, (1, Action(0xF753, 1))));
            Assert.Equal(1, rewriter.SessionResets);
            Assert.Equal(new uint[] { 1 }, Sequences(first));

            byte[] second = rewriter.Rewrite(ClientPacket(2, 0x5679, (202, Action(0xF753, 2))));
            Assert.Equal(new uint[] { 202 }, Sequences(second));
        }
    }
}
