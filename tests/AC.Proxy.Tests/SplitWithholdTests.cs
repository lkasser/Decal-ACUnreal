using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using AC.Protocol;
using AC.Proxy;
using Xunit;

namespace AC.Proxy.Tests
{
    /// <summary>
    /// Taking a message in several fragments out of the stream: the answer to an appraisal the
    /// host alone asked for, kept from the client because AC:Unreal opens its examine panel for
    /// every answer it gets - and an item with many spells is answered in more than one fragment.
    /// The bar is the one a single fragment is held to: the client sees one unbroken run of
    /// numbers, every message it does get whole, and none of the one taken out.
    /// </summary>
    /// <remarks>
    /// The packets are laid out the way ACE sends them (NetworkSession.SendBundle): at most 448
    /// bytes of a message to a fragment, and a message's last fragment often first, in the room a
    /// smaller message before it left, with the rest to follow in the next packets.
    /// </remarks>
    public class SplitWithholdTests
    {
        private const uint GameEvent = 0xF7B0;
        private const uint IdentifyObjectResponse = 0x00C9;
        private const uint ServerMessage = 0xF7E0;
        private const uint Pants = 0x80011B75;
        private const uint Robe = 0x80011B76;

        /// <summary>
        /// An appraisal answer as ACE writes one: the GameEvent opcode, the recipient, the event
        /// sequence, the event, the object - then, standing in for its property tables, spells and
        /// profiles, bytes that differ along its length so that a misplaced fragment shows.
        /// </summary>
        private static byte[] Answer(uint objectId, int size)
        {
            byte[] message = new byte[size];
            for (int i = 20; i < size; i++)
                message[i] = (byte)(i * 7 + (objectId & 0xFF));

            BinaryPrimitives.WriteUInt32LittleEndian(message, GameEvent);
            BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(4), 0x50000006);
            BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(8), 0x31);
            BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(12), IdentifyObjectResponse);
            BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(16), objectId);
            return message;
        }

        /// <summary>A small message of any other kind, marked so it can be told apart.</summary>
        private static byte[] Other(byte marker)
        {
            byte[] message = new byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(message, ServerMessage);
            message[4] = marker;
            return message;
        }

        /// <summary>A message's fragments, numbered <paramref name="sequence"/>, as ACE cuts it.</summary>
        private static IReadOnlyList<AcFragment> Fragments(byte[] message, uint sequence)
            => PacketWriter.Fragment(BinaryPrimitives.ReadUInt32LittleEndian(message), message.AsSpan(4), sequence);

        /// <summary>A server packet, encrypted as a live one is.</summary>
        private static byte[] ServerPacket(uint packetSequence, params AcFragment[] fragments)
        {
            PacketHeaderFlags flags = PacketHeaderFlags.EncryptedChecksum;
            if (fragments.Length > 0)
                flags |= PacketHeaderFlags.BlobFragments;

            return PacketWriter.BuildEncrypted(new PacketHeader { Sequence = packetSequence, Flags = flags }, ReadOnlySpan<byte>.Empty, fragments, 0x1000 + packetSequence);
        }

        private static AcPacket Parse(byte[] datagram)
        {
            Assert.True(AcPacket.TryParse(datagram, out AcPacket packet));
            return packet;
        }

        /// <summary>What the client is given: each fragment's number and index.</summary>
        private static List<(uint Sequence, int Index)> Seen(byte[] datagram)
            => Parse(datagram).Fragments.Select(f => (f.Header.Sequence, (int)f.Header.Index)).ToList();

        /// <summary>Takes out every answer about <paramref name="objectId"/>, as AppraisalRequests would.</summary>
        private static ClientStreamRewriter TakingOut(uint objectId, List<(uint Opcode, byte[] Payload)> handedOn, List<uint> offered = null)
        {
            ClientStreamRewriter rewriter = new ClientStreamRewriter
            {
                ArrivesInOrder = false,
                Withhold = (opcode, payload) =>
                {
                    if (opcode == GameEvent && payload.Length >= 16)
                        offered?.Add(BinaryPrimitives.ReadUInt32LittleEndian(payload.Span.Slice(12)));

                    return opcode == GameEvent && payload.Length >= 16
                        && BinaryPrimitives.ReadUInt32LittleEndian(payload.Span.Slice(8)) == IdentifyObjectResponse
                        && BinaryPrimitives.ReadUInt32LittleEndian(payload.Span.Slice(12)) == objectId;
                },
            };

            rewriter.Withheld += (opcode, payload) => handedOn.Add((opcode, payload.ToArray()));
            return rewriter;
        }

        /// <summary>
        /// Everything the client was given, put together the way it would: each number's
        /// fragments must all be there, once each, for the message to count.
        /// </summary>
        private static SortedDictionary<uint, byte[]> Assemble(IEnumerable<byte[]> datagrams)
        {
            Dictionary<uint, SortedDictionary<int, AcFragment>> parts = new Dictionary<uint, SortedDictionary<int, AcFragment>>();
            foreach (byte[] datagram in datagrams)
            {
                foreach (AcFragment fragment in Parse(datagram).Fragments)
                {
                    if (!parts.TryGetValue(fragment.Header.Sequence, out SortedDictionary<int, AcFragment> of))
                        parts[fragment.Header.Sequence] = of = new SortedDictionary<int, AcFragment>();

                    of[fragment.Header.Index] = fragment;
                }
            }

            SortedDictionary<uint, byte[]> messages = new SortedDictionary<uint, byte[]>();
            foreach ((uint sequence, SortedDictionary<int, AcFragment> of) in parts)
            {
                AcFragment first = of.Values.First();
                Assert.Equal(first.Header.Count, of.Count);
                messages[sequence] = of.Values.SelectMany(f => f.Payload.ToArray()).ToArray();
            }

            return messages;
        }

        [Fact]
        public void AnAnswerInTwoFragmentsAcrossPacketsIsTakenOutWhole()
        {
            List<(uint Opcode, byte[] Payload)> handedOn = new List<(uint, byte[])>();
            ClientStreamRewriter rewriter = TakingOut(Pants, handedOn);

            byte[] answer = Answer(Pants, 484);
            IReadOnlyList<AcFragment> parts = Fragments(answer, 11);
            Assert.Equal(2, parts.Count);

            byte[] first = rewriter.Rewrite(ServerPacket(100, Fragments(Other(1), 10)[0], parts[0]));
            Assert.Equal(new[] { (10u, 0) }, Seen(first));
            Assert.Empty(handedOn);

            byte[] second = rewriter.Rewrite(ServerPacket(101, parts[1]));
            AcPacket emptied = Parse(second);
            Assert.Empty(emptied.Fragments);
            Assert.Equal(101u, emptied.Header.Sequence);
            Assert.True(emptied.TryRecoverEncryptionKey(out uint key));
            Assert.Equal(0x1000u + 101, key);

            // Handed on once, whole, the moment its last fragment was in.
            (uint opcode, byte[] payload) = Assert.Single(handedOn);
            Assert.Equal(GameEvent, opcode);
            Assert.Equal(answer.AsSpan(4).ToArray(), payload);

            // The next message takes the number the answer would have had.
            byte[] third = rewriter.Rewrite(ServerPacket(102, Fragments(Other(2), 12)[0]));
            Assert.Equal(new[] { (11u, 0) }, Seen(third));

            Assert.Equal(1, rewriter.WithheldMessages);
            Assert.Equal(1, rewriter.WithheldSplitMessages);
        }

        /// <summary>
        /// Every fragment of the message shares its number, so however many there are and however
        /// they are spread, the messages after it move down by one - not one a fragment.
        /// </summary>
        [Fact]
        public void AThreeFragmentAnswerMovesEveryLaterMessageDownOnceNotOnceAFragment()
        {
            List<(uint Opcode, byte[] Payload)> handedOn = new List<(uint, byte[])>();
            ClientStreamRewriter rewriter = TakingOut(Pants, handedOn);

            byte[] answer = Answer(Pants, 1000);
            IReadOnlyList<AcFragment> parts = Fragments(answer, 5);
            Assert.Equal(3, parts.Count);

            List<byte[]> sent = new List<byte[]>
            {
                rewriter.Rewrite(ServerPacket(100, Fragments(Other(4), 4)[0])),
                rewriter.Rewrite(ServerPacket(101, parts[0])),
                rewriter.Rewrite(ServerPacket(102, Fragments(Other(6), 6)[0], parts[1])),
                rewriter.Rewrite(ServerPacket(103, parts[2], Fragments(Other(7), 7)[0])),
                rewriter.Rewrite(ServerPacket(104, Fragments(Other(8), 8)[0])),
            };

            Assert.Equal(new[] { (4u, 0) }, Seen(sent[0]));
            Assert.Empty(Seen(sent[1]));
            Assert.Equal(new[] { (5u, 0) }, Seen(sent[2]));
            Assert.Equal(new[] { (6u, 0) }, Seen(sent[3]));
            Assert.Equal(new[] { (7u, 0) }, Seen(sent[4]));

            Assert.Equal(answer.AsSpan(4).ToArray(), Assert.Single(handedOn).Payload);
            Assert.Equal(new byte[] { 4, 6, 7, 8 }, Assemble(sent).Values.Select(m => m[4]).ToArray());
        }

        [Fact]
        public void ResentFragmentsOfATakenOutAnswerAreTakenOutAgainAndNotHandedOnTwice()
        {
            List<(uint Opcode, byte[] Payload)> handedOn = new List<(uint, byte[])>();
            ClientStreamRewriter rewriter = TakingOut(Pants, handedOn);

            IReadOnlyList<AcFragment> parts = Fragments(Answer(Pants, 1000), 11);
            AcFragment before = Fragments(Other(1), 10)[0];
            AcFragment between = Fragments(Other(2), 12)[0];

            rewriter.Rewrite(ServerPacket(100, before, parts[0]));

            // The first packet again before the rest has come: the same fragment twice is one.
            Assert.Equal(new[] { (10u, 0) }, Seen(rewriter.Rewrite(ServerPacket(100, before, parts[0]))));

            rewriter.Rewrite(ServerPacket(101, parts[1]));
            Assert.Equal(new[] { (11u, 0) }, Seen(rewriter.Rewrite(ServerPacket(102, between))));
            rewriter.Rewrite(ServerPacket(103, parts[2]));
            Assert.Single(handedOn);

            // All of it resent after it was handed on: taken out again, said nothing, and the
            // message between keeps the number it was first given.
            Assert.Empty(Seen(rewriter.Rewrite(ServerPacket(101, parts[1]))));
            Assert.Equal(new[] { (11u, 0) }, Seen(rewriter.Rewrite(ServerPacket(102, between))));
            Assert.Empty(Seen(rewriter.Rewrite(ServerPacket(103, parts[2]))));

            Assert.Single(handedOn);
            Assert.Equal(1, rewriter.WithheldMessages);
            Assert.Equal(new[] { (12u, 0) }, Seen(rewriter.Rewrite(ServerPacket(104, Fragments(Other(3), 13)[0]))));
        }

        /// <summary>
        /// ACE puts a message's last fragment first whenever it fits beside a smaller message
        /// before it, and the rest follow. That fragment says nothing of what the message is, so
        /// it is held back until the first arrives - and then goes with it.
        /// </summary>
        [Fact]
        public void ALastFragmentThatComesFirstIsHeldBackAndTakenOutWithTheFirst()
        {
            List<(uint Opcode, byte[] Payload)> handedOn = new List<(uint, byte[])>();
            ClientStreamRewriter rewriter = TakingOut(Pants, handedOn);

            byte[] answer = Answer(Pants, 460);
            IReadOnlyList<AcFragment> parts = Fragments(answer, 11);

            byte[] first = rewriter.Rewrite(ServerPacket(100, Fragments(Other(1), 10)[0], parts[1]));
            Assert.Equal(new[] { (10u, 0) }, Seen(first));
            Assert.Equal(1, rewriter.HeldFragments);

            byte[] second = rewriter.Rewrite(ServerPacket(101, parts[0]));
            Assert.Empty(Seen(second));
            Assert.Equal(answer.AsSpan(4).ToArray(), Assert.Single(handedOn).Payload);

            Assert.Equal(new[] { (11u, 0) }, Seen(rewriter.Rewrite(ServerPacket(102, Fragments(Other(2), 12)[0]))));
            Assert.Equal(1, rewriter.WithheldSplitMessages);
        }

        [Fact]
        public void AHeldFragmentGoesBackInBesideItsFirstWhenTheAnswerIsKept()
        {
            List<(uint Opcode, byte[] Payload)> handedOn = new List<(uint, byte[])>();
            ClientStreamRewriter rewriter = TakingOut(Robe, handedOn);

            byte[] answer = Answer(Pants, 460);
            IReadOnlyList<AcFragment> parts = Fragments(answer, 11);

            byte[] first = rewriter.Rewrite(ServerPacket(100, Fragments(Other(1), 10)[0], parts[1]));
            byte[] second = rewriter.Rewrite(ServerPacket(101, parts[0]));

            Assert.Equal(new[] { (10u, 0) }, Seen(first));
            Assert.Equal(new[] { (11u, 0), (11u, 1) }, Seen(second));
            Assert.Equal(answer, Assemble(new[] { first, second })[11]);
            Assert.Empty(handedOn);
            Assert.Equal(0, rewriter.WithheldMessages);
        }

        /// <summary>
        /// A message some of which has reached the client cannot be taken out: the client has a
        /// number for it, and every message after would land on the wrong one. Here a later
        /// message passes the held fragment, which goes ahead of it; the first fragment, when it
        /// comes, is not even asked about.
        /// </summary>
        [Fact]
        public void AnAnswerPassedBeforeItsFirstFragmentCameIsNeverTakenOut()
        {
            List<(uint Opcode, byte[] Payload)> handedOn = new List<(uint, byte[])>();
            List<uint> offered = new List<uint>();
            ClientStreamRewriter rewriter = TakingOut(Pants, handedOn, offered);

            byte[] answer = Answer(Pants, 484);
            IReadOnlyList<AcFragment> parts = Fragments(answer, 11);

            byte[] first = rewriter.Rewrite(ServerPacket(100, parts[1], Fragments(Other(2), 12)[0]));
            byte[] second = rewriter.Rewrite(ServerPacket(101, parts[0]));

            Assert.Equal(new[] { (11u, 1), (12u, 0) }, Seen(first));
            Assert.Equal(new[] { (11u, 0) }, Seen(second));
            Assert.Equal(answer, Assemble(new[] { first, second })[11]);
            Assert.DoesNotContain(Pants, offered);
            Assert.Empty(handedOn);
            Assert.Equal(0, rewriter.WithheldMessages);
        }

        /// <summary>
        /// The same when a later fragment went through before there was any test to hold it back
        /// for: it is on the client, so the rest goes too.
        /// </summary>
        [Fact]
        public void AnAnswerPartOfWhichWentThroughBeforeTheTestWasSetIsNeverTakenOut()
        {
            List<(uint Opcode, byte[] Payload)> handedOn = new List<(uint, byte[])>();
            ClientStreamRewriter keeping = TakingOut(Pants, handedOn);
            Func<uint, ReadOnlyMemory<byte>, bool> test = keeping.Withhold;
            keeping.Withhold = null;

            IReadOnlyList<AcFragment> parts = Fragments(Answer(Pants, 484), 11);
            Assert.Equal(new[] { (11u, 1) }, Seen(keeping.Rewrite(ServerPacket(100, parts[1]))));

            keeping.Withhold = test;
            Assert.Equal(new[] { (11u, 0) }, Seen(keeping.Rewrite(ServerPacket(101, parts[0]))));
            Assert.Empty(handedOn);
        }

        /// <summary>
        /// Lines the host puts in the game's chat window ride the same packets. Whether they go in
        /// while an answer is being taken out, or while its last fragment is held back, the client
        /// still sees one unbroken run.
        /// </summary>
        [Fact]
        public void MessagesPutInWhileAnAnswerIsTakenOutOrHeldBackKeepTheRunUnbroken()
        {
            List<(uint Opcode, byte[] Payload)> handedOn = new List<(uint, byte[])>();
            ClientStreamRewriter rewriter = TakingOut(Pants, handedOn);
            List<byte[]> sent = new List<byte[]>();

            // In order, with a line of ours going in between its fragments.
            IReadOnlyList<AcFragment> inOrder = Fragments(Answer(Pants, 484), 2);
            sent.Add(rewriter.Rewrite(ServerPacket(100, Fragments(Other(1), 1)[0])));
            rewriter.Enqueue(ServerMessage, new byte[] { 0xA1, 0, 0, 0, 0, 0, 0, 0 });
            sent.Add(rewriter.Rewrite(ServerPacket(101, inOrder[0])));
            sent.Add(rewriter.Rewrite(ServerPacket(102, inOrder[1])));
            sent.Add(rewriter.Rewrite(ServerPacket(103, Fragments(Other(3), 3)[0])));

            // Last fragment first, with a line of ours going in while it is held.
            IReadOnlyList<AcFragment> tailFirst = Fragments(Answer(Pants, 460), 4);
            rewriter.Enqueue(ServerMessage, new byte[] { 0xA2, 0, 0, 0, 0, 0, 0, 0 });
            sent.Add(rewriter.Rewrite(ServerPacket(104, tailFirst[1])));
            sent.Add(rewriter.Rewrite(ServerPacket(105, tailFirst[0])));

            // And one kept, held back the same way, with a line of ours in front of it.
            byte[] kept = Answer(Robe, 460);
            IReadOnlyList<AcFragment> keptParts = Fragments(kept, 5);
            rewriter.Enqueue(ServerMessage, new byte[] { 0xA3, 0, 0, 0, 0, 0, 0, 0 });
            sent.Add(rewriter.Rewrite(ServerPacket(106, keptParts[1])));
            sent.Add(rewriter.Rewrite(ServerPacket(107, keptParts[0])));
            sent.Add(rewriter.Rewrite(ServerPacket(108, Fragments(Other(6), 6)[0])));

            SortedDictionary<uint, byte[]> client = Assemble(sent);
            Assert.Equal(Enumerable.Range(1, client.Count).Select(n => (uint)n), client.Keys);

            // Ours, the client's other messages and the kept answer, in that order; the two taken
            // out are nowhere.
            Assert.Equal(
                new byte[] { 1, 0xA1, 3, 0xA2, 0xA3, 0x31, 6 },
                client.Values.Select(m => BinaryPrimitives.ReadUInt32LittleEndian(m) == GameEvent ? m[8] : m[4]).ToArray());
            Assert.Equal(kept, client.Values.Single(m => BinaryPrimitives.ReadUInt32LittleEndian(m) == GameEvent));
            Assert.Equal(2, handedOn.Count);
        }

        [Fact]
        public void AStreamOfAnswersSplitEveryWayKeepsTheClientsRunUnbroken()
        {
            List<(uint Opcode, byte[] Payload)> handedOn = new List<(uint, byte[])>();
            ClientStreamRewriter rewriter = TakingOut(Pants, handedOn);
            List<byte[]> sent = new List<byte[]>();
            uint packet = 100;

            for (uint sequence = 1; sequence <= 30; sequence++)
            {
                if (sequence % 5 == 0)
                    rewriter.Enqueue(ServerMessage, new byte[] { 0xEE, 0, 0, 0, 0, 0, 0, 0 });

                bool answer = sequence % 4 == 0;
                IReadOnlyList<AcFragment> parts = Fragments(answer ? Answer(Pants, sequence % 8 == 0 ? 460 : 1000) : Other((byte)sequence), sequence);

                // Every eighth comes last fragment first, as ACE sends one beside a smaller message.
                IEnumerable<AcFragment> order = sequence % 8 == 0 ? parts.Reverse() : parts;
                foreach (AcFragment fragment in order)
                    sent.Add(rewriter.Rewrite(ServerPacket(packet++, fragment)));
            }

            sent.Add(rewriter.Rewrite(ServerPacket(packet, Fragments(Other(99), 31)[0])));

            // 31 of the server's, 7 answers taken out, 6 of ours put in.
            SortedDictionary<uint, byte[]> client = Assemble(sent);
            Assert.Equal(Enumerable.Range(1, 31 - 7 + 6).Select(n => (uint)n), client.Keys);
            Assert.DoesNotContain(client.Values, m => BinaryPrimitives.ReadUInt32LittleEndian(m) == GameEvent);
            Assert.Equal(7, handedOn.Count);
            Assert.Equal(7, rewriter.WithheldSplitMessages);
        }

        /// <summary>
        /// A fragment can be lost for good. The answer is then never handed on - but the stream
        /// goes on numbered as it should, and answers that never complete are not kept for ever.
        /// </summary>
        [Fact]
        public void AnswersWhoseFragmentsNeverAllComeAreLetGoWithoutWedgingTheStream()
        {
            List<(uint Opcode, byte[] Payload)> handedOn = new List<(uint, byte[])>();
            ClientStreamRewriter rewriter = TakingOut(Pants, handedOn);
            List<byte[]> sent = new List<byte[]>();
            uint packet = 100;

            // Forty answers, each missing its second fragment, each followed by another message.
            for (uint n = 0; n < 40; n++)
            {
                sent.Add(rewriter.Rewrite(ServerPacket(packet++, Fragments(Answer(Pants, 1000), 2 * n + 1)[0])));
                sent.Add(rewriter.Rewrite(ServerPacket(packet++, Fragments(Other((byte)n), 2 * n + 2)[0])));
            }

            SortedDictionary<uint, byte[]> client = Assemble(sent);
            Assert.Equal(Enumerable.Range(1, 40).Select(n => (uint)n), client.Keys);
            Assert.Empty(handedOn);
            Assert.Equal(40, rewriter.WithheldMessages);
            Assert.Equal(40 - 16, rewriter.AbandonedMessages);

            // The newest still complete when their last fragments turn up.
            rewriter.Rewrite(ServerPacket(packet++, Fragments(Answer(Pants, 1000), 79)[1]));
            rewriter.Rewrite(ServerPacket(packet, Fragments(Answer(Pants, 1000), 79)[2]));
            Assert.Single(handedOn);
        }

        // ------------------------------------------------------------------- ACE's own order

        /// <summary>
        /// ACE numbers a whole bundle, then sends its small messages wherever they fit - so at
        /// login a message numbered a couple of hundred past the rest arrives first, as in
        /// session-20260929-1208.acap, where 217's last fragment came with 143 and 144. Once a
        /// line of ours has gone in, the late ones must still get the numbers their place gives
        /// them, not the latest offset: that lands them on numbers already used, and the client
        /// waits for ever for the ones nobody got.
        /// </summary>
        [Fact]
        public void MessagesThatArriveLateKeepTheirPlaceAfterALineOfOursWentIn()
        {
            ClientStreamRewriter rewriter = TakingOut(Pants, new List<(uint, byte[])>());
            List<byte[]> sent = new List<byte[]>();

            sent.Add(rewriter.Rewrite(ServerPacket(100, Fragments(Other(1), 1)[0], Fragments(Other(2), 2)[0])));
            rewriter.Enqueue(ServerMessage, new byte[] { 0xA1, 0, 0, 0, 0, 0, 0, 0 });
            sent.Add(rewriter.Rewrite(ServerPacket(101, Fragments(Other(3), 3)[0])));

            // A bundle of 4 to 8, sent as ACE sends it: 8 and 7 first, where they fit.
            sent.Add(rewriter.Rewrite(ServerPacket(102, Fragments(Other(4), 4)[0], Fragments(Other(8), 8)[0])));
            sent.Add(rewriter.Rewrite(ServerPacket(103, Fragments(Other(7), 7)[0], Fragments(Other(5), 5)[0])));
            sent.Add(rewriter.Rewrite(ServerPacket(104, Fragments(Other(6), 6)[0])));
            sent.Add(rewriter.Rewrite(ServerPacket(105, Fragments(Other(9), 9)[0])));

            SortedDictionary<uint, byte[]> client = Assemble(sent);
            Assert.Equal(Enumerable.Range(1, 10).Select(n => (uint)n), client.Keys);
            Assert.Equal(new byte[] { 1, 2, 3, 0xA1, 4, 5, 6, 7, 8, 9 }, client.Values.Select(m => m[4]).ToArray());
            Assert.Equal(0, rewriter.SessionResets);
        }

        [Fact]
        public void MessagesThatArriveLateKeepTheirPlaceAfterAnAnswerWasTakenOut()
        {
            List<(uint Opcode, byte[] Payload)> handedOn = new List<(uint, byte[])>();
            ClientStreamRewriter rewriter = TakingOut(Pants, handedOn);
            List<byte[]> sent = new List<byte[]>();

            sent.Add(rewriter.Rewrite(ServerPacket(100, Fragments(Other(1), 1)[0])));
            sent.Add(rewriter.Rewrite(ServerPacket(101, Fragments(Answer(Pants, 120), 2)[0])));
            sent.Add(rewriter.Rewrite(ServerPacket(102, Fragments(Other(3), 3)[0], Fragments(Other(7), 7)[0])));
            sent.Add(rewriter.Rewrite(ServerPacket(103, Fragments(Other(6), 6)[0], Fragments(Other(4), 4)[0])));
            sent.Add(rewriter.Rewrite(ServerPacket(104, Fragments(Other(5), 5)[0])));

            SortedDictionary<uint, byte[]> client = Assemble(sent);
            Assert.Equal(Enumerable.Range(1, 6).Select(n => (uint)n), client.Keys);
            Assert.Equal(new byte[] { 1, 3, 4, 5, 6, 7 }, client.Values.Select(m => m[4]).ToArray());
            Assert.Single(handedOn);
        }

        /// <summary>
        /// The same order means a number far behind the furthest seen is no sign of a new session
        /// from the server - the login in session-20260929-1208.acap went 23 after 253 - and only
        /// the server's handshake starts its numbering over.
        /// </summary>
        [Fact]
        public void TheServersOwnOrderIsNotTakenForANewSession()
        {
            ClientStreamRewriter rewriter = TakingOut(Pants, new List<(uint, byte[])>());
            rewriter.Enqueue(ServerMessage, new byte[] { 0xA1, 0, 0, 0, 0, 0, 0, 0 });

            rewriter.Rewrite(ServerPacket(84, Fragments(Other(1), 143)[0], Fragments(Other(2), 144)[0], Fragments(Other(9), 217)[0]));
            byte[] next = rewriter.Rewrite(ServerPacket(85, Fragments(Other(3), 145)[0]));

            // Ours went in after 217, so 145 keeps its own number.
            Assert.Equal(0, rewriter.SessionResets);
            Assert.Equal(new[] { (145u, 0) }, Seen(next));
        }

        [Fact]
        public void ANewSessionForgetsWhatWasHeldBack()
        {
            List<(uint Opcode, byte[] Payload)> handedOn = new List<(uint, byte[])>();
            ClientStreamRewriter rewriter = TakingOut(Pants, handedOn);

            rewriter.Rewrite(ServerPacket(100, Fragments(Other(1), 10)[0], Fragments(Answer(Pants, 460), 11)[1]));

            // The server's handshake for a new session: nothing of the old one is let go into it.
            byte[] handshake = PacketWriter.Build(new PacketHeader { Sequence = 0, Flags = PacketHeaderFlags.ConnectRequest }, new byte[32]);
            rewriter.Rewrite(handshake);
            Assert.Equal(1, rewriter.SessionResets);

            Assert.Equal(new[] { (1u, 0), (2u, 0) }, Seen(rewriter.Rewrite(ServerPacket(2, Fragments(Other(1), 1)[0], Fragments(Other(2), 2)[0]))));
            Assert.Empty(handedOn);
        }
    }
}
