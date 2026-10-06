using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AC.Host.Transport;
using AC.Protocol;
using AC.Proxy;
using Xunit;

namespace AC.Host.Tests
{
    /// <summary>
    /// The host's own appraisals through the real relay - a client and a server on loopback, the
    /// relay between them, and the host on the relay - as Virindi Tank's mana upkeep makes them
    /// every few seconds. The relay sees them go on in the client's packets, beside the client's
    /// own; they are the host's all the same. Their answers are kept from the client, whose
    /// appraisal panel would open for each, and they are never the player's selection, which
    /// Decal plugins hear of as ItemSelected - Mag-Tools printing every one of them in the chat.
    /// </summary>
    public sealed class HostAppraisalRelayTests
    {
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);
        private const uint Player = 0x50000001;
        private const uint Bracelet = 0x80001234;
        private const uint Ring = 0x80005678;
        private const uint ClientKey = 0x1234ABCD;
        private const uint ServerKey = 0x0BADF00D;

        [Fact]
        public async Task TheHostsOwnAppraisalIsKeptFromTheClientAndIsNotThePlayersSelection()
        {
            int serverPort = FreePortRun(2);
            using Socket server = Bound(serverPort);
            using Socket serverSecond = Bound(serverPort + 1);
            using Socket client = Bound(0);

            int listenPort = FreePortRun(2);
            ProxyTransport transport = new ProxyTransport(new ProxyOptions
            {
                ListenAddress = IPAddress.Loopback,
                ListenPort = listenPort,
                ServerHost = "127.0.0.1",
                ServerPort = serverPort,
                PortCount = 2,
            }, enableActions: true);

            string dataRoot = Path.Combine(Path.GetTempPath(), "achost-relay-appraisal-" + Guid.NewGuid().ToString("N"));
            GameHost host = new GameHost(transport, new ListLog(), dataRoot: dataRoot);
            try
            {
                await host.StartAsync();
                IPEndPoint relay = new IPEndPoint(IPAddress.Loopback, listenPort);
                uint clientFragment = 0, clientPacket = 0, serverFragment = 0, serverPacket = 0;

                // The client is heard from, so the relay knows where it is.
                await client.SendToAsync(ClientPacket(++clientPacket, ++clientFragment, Action(1, 0x01DF, 0)), relay);
                await ReceiveAsync(server);

                // Virindi Tank appraises the bracelet the character wears: it rides out in the
                // client's next packet.
                await OnGameThread(host, () => host.Actions.AppraiseAsync(Bracelet));
                await client.SendToAsync(ClientPacket(++clientPacket, ++clientFragment, Action(2, 0x01DF, 0)), relay);
                byte[] sent = await ReceiveAsync(server);
                Assert.True(AcPacket.TryParse(sent, out AcPacket withOurs));
                Assert.Equal(2, withOurs.Fragments.Count);
                await Until(() => host.Statistics.MessagesOutbound >= 3);

                // Not the player's selection: the client asked nothing about it.
                Assert.Equal(0u, host.Character.SelectedId);

                // Its answer is the host's alone: read, and kept from the client.
                await server.SendToAsync(ServerPacket(++serverPacket, ++serverFragment, Answer(1, Bracelet)), new IPEndPoint(IPAddress.Loopback, listenPort));
                byte[] relayed = await ReceiveAsync(client);
                Assert.False(CarriesAnAnswer(relayed));
                Assert.Equal((1, 0), host.AppraisalsWithheld);
                await Until(() => host.World.TryGet(Bracelet, out AC.Host.World.WorldObject read) && read.HasAppraisalData);

                // The player's own appraisal is the player's: the selection, and its answer theirs.
                await client.SendToAsync(ClientPacket(++clientPacket, ++clientFragment, Action(3, 0x00C8, Ring)), relay);
                await ReceiveAsync(server);
                await Until(() => host.Character.SelectedId == Ring);
                await server.SendToAsync(ServerPacket(++serverPacket, ++serverFragment, Answer(2, Ring)), new IPEndPoint(IPAddress.Loopback, listenPort));
                Assert.True(CarriesAnAnswer(await ReceiveAsync(client)));
                Assert.Equal((1, 0), host.AppraisalsWithheld);
            }
            finally
            {
                await host.DisposeAsync();
                try
                {
                    Directory.Delete(dataRoot, recursive: true);
                }
                catch (DirectoryNotFoundException)
                {
                }
            }
        }

        // ------------------------------------------------------------------- the rig

        private static Task OnGameThread(GameHost host, Func<Task<bool>> action)
        {
            TaskCompletionSource done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            host.RunOnGameThread(() =>
            {
                action();
                done.SetResult();
            });
            return done.Task.WaitAsync(Patience);
        }

        private static async Task Until(Func<bool> condition)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (!condition())
            {
                Assert.True(clock.Elapsed < Patience, "It never happened.");
                await Task.Delay(10);
            }
        }

        /// <summary>
        /// Whether a packet to the client has an appraisal's answer in it - beside whatever the
        /// host put there of its own, a line in the chat.
        /// </summary>
        private static bool CarriesAnAnswer(byte[] datagram)
        {
            Assert.True(AcPacket.TryParse(datagram, out AcPacket packet));
            foreach (AcFragment fragment in packet.Fragments)
            {
                ReadOnlySpan<byte> body = fragment.Payload.Span;
                if (body.Length >= 16 && BinaryPrimitives.ReadUInt32LittleEndian(body) == 0xF7B0 && BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(12)) == 0x00C9)
                    return true;
            }

            return false;
        }

        /// <summary>A client game action: its sequence, its type and an object.</summary>
        private static byte[] Action(uint sequence, uint type, uint objectId)
        {
            byte[] payload = new byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0), sequence);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), type);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), objectId);
            return payload;
        }

        /// <summary>
        /// The server's IdentifyObjectResponse: recipient, sequence, the event, then the object,
        /// no properties, and success.
        /// </summary>
        private static byte[] Answer(uint sequence, uint objectId)
        {
            byte[] payload = new byte[24];
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0), Player);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), sequence);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), 0x00C9);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12), objectId);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(16), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(20), 1);
            return payload;
        }

        /// <summary>A packet as a live client sends one: its checksum keyed, so the relay can put a message of the host's in it.</summary>
        private static byte[] ClientPacket(uint packetSequence, uint fragmentSequence, byte[] action)
            => Packet(packetSequence, PacketWriter.Fragment(0xF7B1, action, fragmentSequence), ClientKey + packetSequence);

        private static byte[] ServerPacket(uint packetSequence, uint fragmentSequence, byte[] gameEvent)
            => Packet(packetSequence, PacketWriter.Fragment(0xF7B0, gameEvent, fragmentSequence), ServerKey + packetSequence);

        private static byte[] Packet(uint packetSequence, IReadOnlyList<AcFragment> fragments, uint key)
            => PacketWriter.BuildEncrypted(
                new PacketHeader { Sequence = packetSequence, Flags = PacketHeaderFlags.EncryptedChecksum | PacketHeaderFlags.BlobFragments },
                ReadOnlySpan<byte>.Empty,
                fragments,
                key);

        private static Socket Bound(int port)
        {
            Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
            return socket;
        }

        private static async Task<byte[]> ReceiveAsync(Socket socket)
        {
            byte[] buffer = new byte[AcPacket.MaxDatagramSize];
            using CancellationTokenSource timeout = new CancellationTokenSource(Patience);
            SocketReceiveFromResult result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), timeout.Token);
            return buffer.AsSpan(0, result.ReceivedBytes).ToArray();
        }

        /// <summary>
        /// A run of free consecutive UDP ports on loopback, never the player's: binding each to
        /// check leaves a moment before the relay binds them, the accepted cost of real sockets.
        /// </summary>
        private static int FreePortRun(int count)
        {
            for (int attempt = 0; attempt < 50; attempt++)
            {
                using Socket probe = Bound(0);
                int first = ((IPEndPoint)probe.LocalEndPoint).Port;
                bool free = true;
                for (int offset = 1; offset < count && free; offset++)
                {
                    try
                    {
                        using Socket next = Bound(first + offset);
                    }
                    catch (SocketException)
                    {
                        free = false;
                    }
                }

                if (free)
                    return first;
            }

            throw new InvalidOperationException("Could not find free consecutive ports.");
        }
    }
}
