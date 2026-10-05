using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AC.Protocol;
using AC.Proxy;
using Xunit;

namespace AC.Proxy.Tests
{
    /// <summary>
    /// A relay stopped and another started on the same ports while the game stays connected: the
    /// second carries the first one's numbering on, so neither end sees anything but one unbroken
    /// stream - the same, number for number and byte for byte, as a relay that never stopped.
    /// </summary>
    /// <remarks>
    /// The packets are laid out as in <see cref="SplitWithholdTests"/>: encrypted as live ones are,
    /// so a rewriter can re-stamp them, and cut as ACE cuts its messages.
    /// </remarks>
    public class HandoverTests
    {
        private const uint GameEvent = 0xF7B0;
        private const uint IdentifyObjectResponse = 0x00C9;
        private const uint ServerMessage = 0xF7E0;
        private const uint GameAction = 0xF7B1;
        private const uint Pants = 0x80011B75;

        /// <summary>A small message, marked so it can be told apart.</summary>
        private static byte[] Marked(uint opcode, byte marker)
        {
            byte[] message = new byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(message, opcode);
            message[4] = marker;
            return message;
        }

        /// <summary>An appraisal answer as ACE writes one, long enough to need several fragments.</summary>
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

        private static IReadOnlyList<AcFragment> Fragments(byte[] message, uint sequence)
            => PacketWriter.Fragment(BinaryPrimitives.ReadUInt32LittleEndian(message), message.AsSpan(4), sequence);

        /// <summary>A packet encrypted as a live one is, so a rewriter can recover its key.</summary>
        private static byte[] Packet(uint packetSequence, params AcFragment[] fragments)
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

        /// <summary>Every fragment number the far end is given, in the order it is given them.</summary>
        private static List<uint> Numbers(IEnumerable<byte[]> datagrams)
            => datagrams.SelectMany(d => Parse(d).Fragments.Where(f => f.Header.Index == 0).Select(f => f.Header.Sequence)).ToList();

        /// <summary>A rewriter's state, written and read back as a handover file would carry it.</summary>
        private static RewriterState ThroughBytes(RewriterState state)
        {
            RelayState relay = new RelayState { Inbound = state };
            using MemoryStream stream = new MemoryStream();
            using (BinaryWriter writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
                relay.Write(writer);

            stream.Position = 0;
            using BinaryReader reader = new BinaryReader(stream);
            return RelayState.Read(reader).Inbound;
        }

        /// <summary>
        /// Ten messages from the server with one of the host's lines put in after the third, the
        /// relay stopped after <paramref name="stopAfter"/> packets and another taking its place.
        /// </summary>
        private static List<byte[]> ServerStreamWithALine(int stopAfter, bool carryOn)
        {
            ClientStreamRewriter rewriter = new ClientStreamRewriter { ArrivesInOrder = false };
            List<byte[]> given = new List<byte[]>();

            for (uint n = 1; n <= 10; n++)
            {
                if (n == 3)
                    rewriter.Enqueue(ServerMessage, Marked(ServerMessage, 0xEE).AsMemory(4));

                given.Add(rewriter.Rewrite(Packet(100 + n, Fragments(Marked(ServerMessage, (byte)n), n)[0])));

                if (n == stopAfter)
                {
                    RewriterState left = rewriter.SaveState();
                    rewriter = new ClientStreamRewriter { ArrivesInOrder = false };
                    if (carryOn)
                        rewriter.RestoreState(ThroughBytes(left));
                }
            }

            return given;
        }

        [Fact]
        public void ALineInjectedBeforeTheRelayStoppedKeepsTheClientsNumberingWholeAfterIt()
        {
            List<byte[]> uninterrupted = ServerStreamWithALine(stopAfter: 0, carryOn: true);
            List<byte[]> carriedOn = ServerStreamWithALine(stopAfter: 5, carryOn: true);

            // The line took number 4, so every message of the server's after it is one up - and
            // stays one up through the second relay: eleven numbers, each once, in order.
            Assert.Equal(Enumerable.Range(1, 11).Select(n => (uint)n), Numbers(carriedOn));
            Assert.Equal(uninterrupted, carriedOn);
        }

        /// <summary>What the handover is for: a relay that forgot gives the client numbers it already has.</summary>
        [Fact]
        public void ARelayThatForgotWouldGiveTheClientNumbersItAlreadyHas()
        {
            List<uint> numbers = Numbers(ServerStreamWithALine(stopAfter: 5, carryOn: false));

            Assert.Equal(new uint[] { 1, 2, 3, 4, 5, 6, 6, 7, 8, 9, 10 }, numbers);
        }

        [Fact]
        public void AnActionInjectedBeforeTheRelayStoppedKeepsTheServersNumberingWholeAfterIt()
        {
            List<byte[]> Run(int stopAfter)
            {
                ClientStreamRewriter rewriter = new ClientStreamRewriter();
                List<byte[]> given = new List<byte[]>();
                for (uint n = 1; n <= 8; n++)
                {
                    if (n == 2)
                        rewriter.Enqueue(GameAction, Marked(GameAction, 0xAA).AsMemory(4));

                    given.Add(rewriter.Rewrite(Packet(50 + n, Fragments(Marked(GameAction, (byte)n), 20 + n)[0])));

                    if (n == stopAfter)
                    {
                        RewriterState left = rewriter.SaveState();
                        rewriter = new ClientStreamRewriter();
                        rewriter.RestoreState(ThroughBytes(left));
                    }
                }

                return given;
            }

            List<byte[]> carriedOn = Run(stopAfter: 4);
            Assert.Equal(Enumerable.Range(21, 9).Select(n => (uint)n), Numbers(carriedOn));
            Assert.Equal(Run(stopAfter: 0), carriedOn);
        }

        /// <summary>
        /// The relay stops between a split answer's last fragment, which ACE sent first and the
        /// relay held back, and its first: the next relay takes the held fragment up, takes the
        /// whole answer out, hands it on, and numbers what follows as the first relay would have.
        /// </summary>
        [Fact]
        public void AFragmentHeldBackWhenTheRelayStoppedIsTakenOutWithItsFirstByTheNext()
        {
            byte[] answer = Answer(Pants, 460);

            (List<byte[]> Given, List<byte[]> HandedOn) Run(bool stopBetween)
            {
                List<byte[]> handedOn = new List<byte[]>();
                ClientStreamRewriter TakingOut()
                {
                    ClientStreamRewriter made = new ClientStreamRewriter
                    {
                        ArrivesInOrder = false,
                        Withhold = (opcode, payload) => opcode == GameEvent && payload.Length >= 16
                            && BinaryPrimitives.ReadUInt32LittleEndian(payload.Span.Slice(8)) == IdentifyObjectResponse
                            && BinaryPrimitives.ReadUInt32LittleEndian(payload.Span.Slice(12)) == Pants,
                    };
                    made.Withheld += (_, payload) => handedOn.Add(payload.ToArray());
                    return made;
                }

                IReadOnlyList<AcFragment> parts = Fragments(answer, 11);
                ClientStreamRewriter rewriter = TakingOut();
                List<byte[]> given = new List<byte[]>
                {
                    rewriter.Rewrite(Packet(100, Fragments(Marked(ServerMessage, 1), 10)[0], parts[1])),
                };

                Assert.Equal(1, rewriter.HeldFragments);
                if (stopBetween)
                {
                    RewriterState left = ThroughBytes(rewriter.SaveState());
                    Assert.Single(left.Held);
                    rewriter = TakingOut();
                    rewriter.RestoreState(left);
                }

                given.Add(rewriter.Rewrite(Packet(101, parts[0])));
                given.Add(rewriter.Rewrite(Packet(102, Fragments(Marked(ServerMessage, 2), 12)[0])));
                return (given, handedOn);
            }

            (List<byte[]> uninterrupted, List<byte[]> heardWhole) = Run(stopBetween: false);
            (List<byte[]> carriedOn, List<byte[]> heardCarried) = Run(stopBetween: true);

            Assert.Equal(new uint[] { 10, 11 }, Numbers(carriedOn));
            Assert.Equal(uninterrupted, carriedOn);
            Assert.Equal(answer.AsSpan(4).ToArray(), Assert.Single(heardCarried));
            Assert.Equal(heardWhole, heardCarried);
        }

        /// <summary>Everything a rewriter keeps comes back from the bytes as it went in.</summary>
        [Fact]
        public void ARelaysStateRoundTripsThroughItsBytes()
        {
            ClientStreamRewriter rewriter = new ClientStreamRewriter
            {
                ArrivesInOrder = false,
                Withhold = (opcode, payload) => opcode == GameEvent,
            };

            byte[] answer = Answer(Pants, 1000);
            IReadOnlyList<AcFragment> parts = Fragments(answer, 7);
            rewriter.Enqueue(ServerMessage, Marked(ServerMessage, 9).AsMemory(4));
            // A line put in, an answer being taken out with a fragment still to come, and the last
            // fragment of another held back for its first.
            rewriter.Rewrite(Packet(1, Fragments(Marked(ServerMessage, 1), 5)[0]));
            rewriter.Rewrite(Packet(2, parts[0], parts[1]));
            rewriter.Rewrite(Packet(3, Fragments(Marked(ServerMessage, 2), 8)[0]));
            rewriter.Rewrite(Packet(4, Fragments(Answer(Pants, 600), 9)[1]));

            RelayState relay = new RelayState { Inbound = rewriter.SaveState(), Outbound = new ClientStreamRewriter().SaveState() };
            relay.Clients[9100] = new IPEndPoint(IPAddress.Loopback, 51234);
            relay.InboundPartials.AddRange(Fragments(Answer(0x80000001, 900), 33).Take(1));

            byte[] Bytes(RelayState state)
            {
                using MemoryStream stream = new MemoryStream();
                using (BinaryWriter writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
                    state.Write(writer);
                return stream.ToArray();
            }

            byte[] written = Bytes(relay);
            RelayState read;
            using (BinaryReader reader = new BinaryReader(new MemoryStream(written)))
                read = RelayState.Read(reader);

            Assert.Equal(written, Bytes(read));
            Assert.Equal(new IPEndPoint(IPAddress.Loopback, 51234), read.Clients[9100]);
            Assert.Single(read.Inbound.Taking);
            Assert.Single(read.Inbound.Held);
            Assert.Equal(relay.Inbound.Offset, read.Inbound.Offset);
            Assert.Equal(relay.Inbound.Shifts, read.Inbound.Shifts);
            Assert.Equal(relay.Inbound.Withheld, read.Inbound.Withheld);

            // And put back into a rewriter, it is the same state again.
            ClientStreamRewriter restored = new ClientStreamRewriter { ArrivesInOrder = false };
            restored.RestoreState(read.Inbound);
            Assert.Equal(Bytes(new RelayState { Inbound = relay.Inbound }), Bytes(new RelayState { Inbound = restored.SaveState() }));
        }

        [Fact]
        public void BytesThatAreNotARelaysStateAreRefused()
        {
            using BinaryReader reader = new BinaryReader(new MemoryStream(new byte[] { 7, 0, 0, 0 }));
            Assert.Throws<InvalidDataException>(() => RelayState.Read(reader));
        }

        [Fact]
        public void AnAssemblersHalfMessagesCarryOverToAnother()
        {
            byte[] answer = Answer(Pants, 1000);
            IReadOnlyList<AcFragment> parts = Fragments(answer, 40);

            MessageAssembler before = new MessageAssembler();
            Assert.False(before.TryAccept(parts[0], out _));
            Assert.False(before.TryAccept(parts[2], out _));

            MessageAssembler after = new MessageAssembler();
            foreach (AcFragment fragment in before.SavePartials())
                Assert.False(after.TryAccept(fragment, out _));

            Assert.True(after.TryAccept(parts[1], out AcMessage message));
            Assert.Equal(answer.AsSpan(4).ToArray(), message.Payload.ToArray());
            Assert.Equal(40u, message.FragmentSequence);
        }

        // ------------------------------------------------------------------- over real sockets

        private static int FreePortRun(int count)
        {
            for (int attempt = 0; attempt < 50; attempt++)
            {
                using Socket probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                int first = ((IPEndPoint)probe.LocalEndPoint).Port;

                bool allFree = true;
                for (int offset = 1; offset < count && allFree; offset++)
                {
                    try
                    {
                        using Socket next = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                        next.Bind(new IPEndPoint(IPAddress.Loopback, first + offset));
                    }
                    catch (SocketException)
                    {
                        allFree = false;
                    }
                }

                if (allFree)
                    return first;
            }

            throw new InvalidOperationException("Could not find free consecutive ports.");
        }

        private static async Task<byte[]> ReceiveAsync(Socket socket)
        {
            byte[] buffer = new byte[AcPacket.MaxDatagramSize];
            using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            SocketReceiveFromResult result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), timeout.Token);
            return buffer.AsSpan(0, result.ReceivedBytes).ToArray();
        }

        private static AcProxy Relay(int listenPort, int serverPort)
            => new AcProxy(new ProxyOptions
            {
                ListenAddress = IPAddress.Loopback,
                ListenPort = listenPort,
                ServerHost = "127.0.0.1",
                ServerPort = serverPort,
                PortCount = 1,
            })
            {
                Rewriter = new ClientStreamRewriter(),
                InboundRewriter = new ClientStreamRewriter { ArrivesInOrder = false },
            };

        /// <summary>
        /// The whole of it over sockets: a line put in, the relay stopped and another started on
        /// the same port, and the server speaking first - as after a restart it may. The second
        /// relay knows the client already, so the server's packet reaches it rather than being
        /// sent back to the server, numbered one up as the line left it.
        /// </summary>
        [Fact]
        public async Task ARelayStartedOnTheSamePortCarriesTheSessionOnAndKnowsTheClientBeforeItSpeaks()
        {
            int serverPort = FreePortRun(1);
            using Socket server = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            server.Bind(new IPEndPoint(IPAddress.Loopback, serverPort));
            using Socket client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            client.Bind(new IPEndPoint(IPAddress.Loopback, 0));

            int listenPort = FreePortRun(1);
            while (listenPort == serverPort)
                listenPort = FreePortRun(1);
            IPEndPoint relayed = new IPEndPoint(IPAddress.Loopback, listenPort);

            List<uint> numbers = new List<uint>();
            AcProxy first = Relay(listenPort, serverPort);
            try
            {
                await first.StartAsync();

                await client.SendToAsync(Packet(1, Fragments(Marked(GameAction, 1), 1)[0]), SocketFlags.None, relayed);
                await ReceiveAsync(server);

                await server.SendToAsync(Packet(201, Fragments(Marked(ServerMessage, 1), 1)[0]), SocketFlags.None, relayed);
                numbers.AddRange(Numbers(new[] { await ReceiveAsync(client) }));

                first.InboundRewriter.Enqueue(ServerMessage, Marked(ServerMessage, 0xEE).AsMemory(4));
                await server.SendToAsync(Packet(202, Fragments(Marked(ServerMessage, 2), 2)[0]), SocketFlags.None, relayed);
                numbers.AddRange(Numbers(new[] { await ReceiveAsync(client) }));
            }
            finally
            {
                await first.DisposeAsync();
            }

            RelayState left = first.SaveState();

            Assert.Equal(new uint[] { 1, 2, 3 }, numbers);
            Assert.Equal(((IPEndPoint)client.LocalEndPoint).Port, Assert.Single(left.Clients).Value.Port);

            await using AcProxy second = Relay(listenPort, serverPort);
            second.Resume(left);
            await second.StartAsync();

            await server.SendToAsync(Packet(203, Fragments(Marked(ServerMessage, 3), 3)[0]), SocketFlags.None, relayed);
            byte[] reached = await ReceiveAsync(client);
            Assert.Equal(new uint[] { 4 }, Numbers(new[] { reached }));
            Assert.Equal(3, Parse(reached).Fragments[0].Payload.Span[4]);
            Assert.Equal(0, second.DatagramsFromClient);

            // And the client goes on being heard.
            await client.SendToAsync(Packet(2, Fragments(Marked(GameAction, 2), 2)[0]), SocketFlags.None, relayed);
            Assert.Equal(new uint[] { 2 }, Numbers(new[] { await ReceiveAsync(server) }));
        }
    }
}
