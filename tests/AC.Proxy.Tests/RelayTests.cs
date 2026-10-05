using System;
using System.Collections.Generic;
using System.IO;
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
    /// The relay under test sits between a fake client socket and a fake server
    /// socket, all on loopback - the same shape as a self-hosted ACE server, which is
    /// also the case where telling client from server is hardest.
    /// </summary>
    public class RelayTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Finds a run of free consecutive UDP ports. Binding each to check leaves a
        /// small window before the proxy binds them, which is the accepted cost of
        /// testing real sockets.
        /// </summary>
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

        private static async Task<(byte[] bytes, IPEndPoint from)> ReceiveAsync(Socket socket)
        {
            byte[] buffer = new byte[AcPacket.MaxDatagramSize];
            using CancellationTokenSource cts = new CancellationTokenSource(Timeout);

            SocketReceiveFromResult result = await socket.ReceiveFromAsync(
                buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cts.Token);

            byte[] bytes = new byte[result.ReceivedBytes];
            Array.Copy(buffer, bytes, result.ReceivedBytes);

            return (bytes, (IPEndPoint)result.RemoteEndPoint);
        }

        private static byte[] SamplePacket(uint sequence)
        {
            IReadOnlyList<AcFragment> fragments = PacketWriter.Fragment(0xF7B0, new byte[] { 1, 2, 3, 4 }, sequence);
            return PacketWriter.Build(
                new PacketHeader { Sequence = sequence, Flags = PacketHeaderFlags.BlobFragments },
                ReadOnlySpan<byte>.Empty,
                fragments);
        }

        private sealed class Harness : IAsyncDisposable
        {
            public Socket Server { get; }

            /// <summary>The server's second port, bound like ACE binds Port+1.</summary>
            public Socket ServerSecond { get; }

            public Socket Client { get; }

            public AcProxy Proxy { get; }

            public int ListenPort { get; }

            public int ServerPort { get; }

            public List<PacketObservedEventArgs> Observed { get; } = new List<PacketObservedEventArgs>();

            private Harness(Socket server, Socket serverSecond, Socket client, AcProxy proxy, int listenPort, int serverPort)
            {
                Server = server;
                ServerSecond = serverSecond;
                Client = client;
                Proxy = proxy;
                ListenPort = listenPort;
                ServerPort = serverPort;
            }

            public static async Task<Harness> StartAsync(int portCount = 2)
            {
                int serverPort = FreePortRun(portCount);

                Socket server = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                server.Bind(new IPEndPoint(IPAddress.Loopback, serverPort));

                // ACE holds every port in its range, so a client can never be allocated
                // one of them. Binding the second here keeps the harness honest about
                // that; without it, sequential ephemeral allocation on Windows tends to
                // hand the client exactly serverPort + 1.
                Socket serverSecond = null;
                if (portCount > 1)
                {
                    serverSecond = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    serverSecond.Bind(new IPEndPoint(IPAddress.Loopback, serverPort + 1));
                }

                Socket client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                client.Bind(new IPEndPoint(IPAddress.Loopback, 0));

                int listenPort = FreePortRun(portCount);

                AcProxy proxy = new AcProxy(new ProxyOptions
                {
                    ListenAddress = IPAddress.Loopback,
                    ListenPort = listenPort,
                    ServerHost = "127.0.0.1",
                    ServerPort = serverPort,
                    PortCount = portCount,
                });

                Harness harness = new Harness(server, serverSecond, client, proxy, listenPort, serverPort);
                proxy.PacketObserved += (_, e) =>
                {
                    lock (harness.Observed)
                        harness.Observed.Add(e);
                };

                await proxy.StartAsync();
                return harness;
            }

            public IPEndPoint ProxyEndPoint(int offset = 0) => new IPEndPoint(IPAddress.Loopback, ListenPort + offset);

            public async ValueTask DisposeAsync()
            {
                await Proxy.DisposeAsync();
                Server.Dispose();
                ServerSecond?.Dispose();
                Client.Dispose();
            }
        }

        [Fact]
        public async Task ClientDatagramsReachTheServerUnchanged()
        {
            await using Harness h = await Harness.StartAsync();
            byte[] packet = SamplePacket(1);

            await h.Client.SendToAsync(packet, SocketFlags.None, h.ProxyEndPoint());
            (byte[] received, IPEndPoint from) = await ReceiveAsync(h.Server);

            Assert.Equal(packet, received);
            Assert.Equal(h.ListenPort, from.Port);
        }

        [Fact]
        public async Task ServerRepliesReachTheClientUnchanged()
        {
            await using Harness h = await Harness.StartAsync();

            // The proxy learns the client from its first datagram; a reply before that
            // has nowhere to go.
            await h.Client.SendToAsync(SamplePacket(1), SocketFlags.None, h.ProxyEndPoint());
            (_, IPEndPoint proxyAsSeenByServer) = await ReceiveAsync(h.Server);

            byte[] reply = SamplePacket(2);
            await h.Server.SendToAsync(reply, SocketFlags.None, proxyAsSeenByServer);
            (byte[] received, IPEndPoint from) = await ReceiveAsync(h.Client);

            Assert.Equal(reply, received);
            Assert.Equal(h.ListenPort, from.Port);
        }

        [Fact]
        public async Task BothDirectionsAreObservedWithTheRightDirection()
        {
            await using Harness h = await Harness.StartAsync();

            await h.Client.SendToAsync(SamplePacket(1), SocketFlags.None, h.ProxyEndPoint());
            (_, IPEndPoint proxyAsSeenByServer) = await ReceiveAsync(h.Server);

            await h.Server.SendToAsync(SamplePacket(2), SocketFlags.None, proxyAsSeenByServer);
            await ReceiveAsync(h.Client);

            await WaitForObservedAsync(h, 2);

            lock (h.Observed)
            {
                Assert.Equal(PacketDirection.Outbound, h.Observed[0].Direction);
                Assert.Equal(PacketDirection.Inbound, h.Observed[1].Direction);
                Assert.True(h.Observed[0].Parsed);
                Assert.True(h.Observed[1].Parsed);
                Assert.Equal(1u, h.Observed[0].Packet.Header.Sequence);
                Assert.Equal(2u, h.Observed[1].Packet.Header.Sequence);
            }

            Assert.Equal(1, h.Proxy.DatagramsFromClient);
            Assert.Equal(1, h.Proxy.DatagramsFromServer);
        }

        [Fact]
        public async Task BytesThatDoNotParseAreStillForwarded()
        {
            // The relay must never depend on understanding the traffic.
            await using Harness h = await Harness.StartAsync();
            byte[] garbage = { 0xDE, 0xAD };

            await h.Client.SendToAsync(garbage, SocketFlags.None, h.ProxyEndPoint());
            (byte[] received, _) = await ReceiveAsync(h.Server);

            Assert.Equal(garbage, received);

            await WaitForObservedAsync(h, 1);

            lock (h.Observed)
                Assert.False(h.Observed[0].Parsed);

            Assert.Equal(1, h.Proxy.UnparsedDatagrams);
        }

        [Fact]
        public async Task TheSecondPortRelaysToTheSecondServerPort()
        {
            await using Harness h = await Harness.StartAsync(portCount: 2);

            byte[] packet = SamplePacket(7);
            await h.Client.SendToAsync(packet, SocketFlags.None, h.ProxyEndPoint(offset: 1));
            (byte[] received, IPEndPoint from) = await ReceiveAsync(h.ServerSecond);

            Assert.Equal(packet, received);
            Assert.Equal(h.ListenPort + 1, from.Port);

            await WaitForObservedAsync(h, 1);
            lock (h.Observed)
                Assert.Equal(h.ListenPort + 1, h.Observed[0].LocalPort);
        }

        [Fact]
        public async Task AServerReplyFromItsOtherPortIsStillRecognizedAsTheServer()
        {
            // ACE may answer a datagram sent to port P from port P+1. That reply must be
            // classified as inbound, not mistaken for a new client.
            await using Harness h = await Harness.StartAsync(portCount: 2);

            await h.Client.SendToAsync(SamplePacket(1), SocketFlags.None, h.ProxyEndPoint());
            (_, IPEndPoint proxyAsSeenByServer) = await ReceiveAsync(h.Server);

            byte[] reply = SamplePacket(2);
            await h.ServerSecond.SendToAsync(reply, SocketFlags.None, proxyAsSeenByServer);
            (byte[] received, _) = await ReceiveAsync(h.Client);

            Assert.Equal(reply, received);

            await WaitForObservedAsync(h, 2);
            lock (h.Observed)
                Assert.Equal(PacketDirection.Inbound, h.Observed[1].Direction);
        }

        [Fact]
        public async Task ListeningEndpointsMatchTheConfiguredRange()
        {
            await using Harness h = await Harness.StartAsync(portCount: 2);

            IReadOnlyList<IPEndPoint> listening = h.Proxy.ListeningOn;

            Assert.Equal(2, listening.Count);
            Assert.Equal(h.ListenPort, listening[0].Port);
            Assert.Equal(h.ListenPort + 1, listening[1].Port);
        }

        [Fact]
        public async Task AnObserverThatThrowsDoesNotStopTheRelay()
        {
            await using Harness h = await Harness.StartAsync();
            h.Proxy.PacketObserved += (_, _) => throw new InvalidOperationException("observer bug");

            await h.Client.SendToAsync(SamplePacket(1), SocketFlags.None, h.ProxyEndPoint());
            await ReceiveAsync(h.Server);

            // A second datagram proves the loop survived the first observer's throw.
            byte[] second = SamplePacket(2);
            await h.Client.SendToAsync(second, SocketFlags.None, h.ProxyEndPoint());
            (byte[] received, _) = await ReceiveAsync(h.Server);

            Assert.Equal(second, received);
        }

        [Fact]
        public async Task AClientWhosePortFallsInTheServerRangeIsStillTheClient()
        {
            // Before any client has spoken, a datagram cannot be a server reply -
            // whatever port it came from. Without this rule a client allocated a port
            // inside the server's range would be misclassified and never forwarded.
            await using Harness h = await Harness.StartAsync(portCount: 3);

            // Port serverPort+2 is in the relayed range and, with portCount 3, not
            // bound by the harness's two server sockets - so a client can hold it. The
            // harness's own client may already have been allocated exactly that port;
            // if so it is the in-range client this test wants.
            int inRangePort = h.ServerPort + 2;
            Socket owned = null;
            Socket clientInRange = h.Client;

            if (((IPEndPoint)h.Client.LocalEndPoint).Port != inRangePort)
            {
                owned = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                owned.Bind(new IPEndPoint(IPAddress.Loopback, inRangePort));
                clientInRange = owned;
            }

            using Socket ownedForDisposal = owned;

            byte[] packet = SamplePacket(3);
            await clientInRange.SendToAsync(packet, SocketFlags.None, h.ProxyEndPoint());
            (byte[] received, _) = await ReceiveAsync(h.Server);

            Assert.Equal(packet, received);
            Assert.Equal(1, h.Proxy.DatagramsFromClient);
            Assert.Equal(0, h.Proxy.DatagramsFromServer);
        }

        [Fact]
        public async Task ForwardingToAPortItListensOnIsRefused()
        {
            // The failure this prevents is silent: the relay echoes every datagram
            // back to the client, which reads as a healthy conversation in a packet
            // log while the real server receives nothing and the game never logs in.
            AcProxy proxy = new AcProxy(new ProxyOptions
            {
                ListenAddress = IPAddress.Loopback,
                ListenPort = 9000,
                ServerHost = "127.0.0.1",
                ServerPort = 9000,
                PortCount = 2,
            });

            InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => proxy.StartAsync());

            Assert.Contains("forward to itself", ex.Message);
            Assert.Empty(proxy.ListeningOn);

            await proxy.DisposeAsync();
        }

        [Fact]
        public async Task AnOverlappingButNotIdenticalPortRangeIsAlsoRefused()
        {
            // Listening on 9001-9002 while forwarding to 9000-9001 loops on 9001 only,
            // which would half-work and be worse to diagnose than a total failure.
            AcProxy proxy = new AcProxy(new ProxyOptions
            {
                ListenAddress = IPAddress.Loopback,
                ListenPort = 9001,
                ServerHost = "127.0.0.1",
                ServerPort = 9000,
                PortCount = 2,
            });

            await Assert.ThrowsAsync<InvalidOperationException>(() => proxy.StartAsync());
            await proxy.DisposeAsync();
        }

        [Fact]
        public async Task ANonOverlappingRangeOnTheSameHostIsFine()
        {
            // The normal local setup: server on 9000-9001, relay on 9100-9101.
            await using Harness h = await Harness.StartAsync();

            Assert.Equal(2, h.Proxy.ListeningOn.Count);
            Assert.Equal(0, h.Proxy.LoopedBackDatagrams);
        }

        [Fact]
        public void OverlapIsDetectedOnPortsAlone()
        {
            Assert.True(new ProxyOptions { ListenPort = 9000, ServerPort = 9000, PortCount = 2 }.PortRangesOverlap);
            Assert.True(new ProxyOptions { ListenPort = 9001, ServerPort = 9000, PortCount = 2 }.PortRangesOverlap);
            Assert.False(new ProxyOptions { ListenPort = 9002, ServerPort = 9000, PortCount = 2 }.PortRangesOverlap);
            Assert.False(new ProxyOptions { ListenPort = 9100, ServerPort = 9000, PortCount = 2 }.PortRangesOverlap);
        }

        [Fact]
        public async Task OverlappingPortsOnADifferentHostAreAllowed()
        {
            // Relaying 9000 on this machine to 9000 on another is the ordinary remote
            // case and must not be caught by the loop check.
            AcProxy proxy = new AcProxy(new ProxyOptions
            {
                ListenAddress = IPAddress.Loopback,
                ListenPort = FreePortRun(1),
                ServerHost = "192.0.2.10",  // TEST-NET-1, reserved for documentation
                ServerPort = FreePortRun(1),
                PortCount = 1,
            });

            await proxy.StartAsync();
            Assert.Single(proxy.ListeningOn);
            await proxy.DisposeAsync();
        }

        [Fact]
        public async Task APortAlreadyInUseExplainsItselfRatherThanThrowingASocketError()
        {
            // Starting a second copy by accident is easy, and a raw SocketException
            // stack trace says nothing about what to do next.
            await using Harness h = await Harness.StartAsync();

            AcProxy second = new AcProxy(new ProxyOptions
            {
                ListenAddress = IPAddress.Loopback,
                ListenPort = h.ListenPort,
                ServerHost = "127.0.0.1",
                ServerPort = h.ServerPort,
                PortCount = 2,
            });

            InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => second.StartAsync());

            Assert.Contains("already in use", ex.Message);
            Assert.Contains("another copy", ex.Message);
            Assert.IsType<SocketException>(ex.InnerException);

            await second.DisposeAsync();
        }

        [Fact]
        public void OptionsRejectAPortRunThatOverflows()
        {
            ProxyOptions options = new ProxyOptions
            {
                ServerHost = "localhost",
                ListenPort = 65535,
                PortCount = 2,
            };

            Assert.Throws<InvalidOperationException>(() => options.Validate());
        }

        [Fact]
        public void OptionsRequireAServer()
        {
            Assert.Throws<InvalidOperationException>(() => new ProxyOptions().Validate());
        }

        private static async Task WaitForObservedAsync(Harness h, int count)
        {
            DateTime deadline = DateTime.UtcNow + Timeout;

            while (DateTime.UtcNow < deadline)
            {
                lock (h.Observed)
                {
                    if (h.Observed.Count >= count)
                        return;
                }

                await Task.Delay(10);
            }

            throw new TimeoutException($"Expected {count} observed packets.");
        }
    }

    public class CaptureFileTests
    {
        [Fact]
        public void RecordsRoundTrip()
        {
            MemoryStream stream = new MemoryStream();
            DateTimeOffset t0 = new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

            using (CaptureWriter writer = new CaptureWriter(stream))
            {
                writer.Write(t0, PacketDirection.Outbound, 9100, new byte[] { 1, 2, 3 });
                writer.Write(t0.AddMilliseconds(5), PacketDirection.Inbound, 9101, new byte[] { 9 });
                writer.Write(t0.AddMilliseconds(9), PacketDirection.Inbound, 9100, Array.Empty<byte>());
                Assert.Equal(3, writer.RecordsWritten);
            }

            List<CapturedDatagram> records = new List<CapturedDatagram>(
                CaptureReader.Read(new MemoryStream(stream.ToArray())));

            Assert.Equal(3, records.Count);

            Assert.Equal(t0, records[0].Timestamp);
            Assert.Equal(PacketDirection.Outbound, records[0].Direction);
            Assert.Equal(9100, records[0].LocalPort);
            Assert.Equal(new byte[] { 1, 2, 3 }, records[0].Bytes);

            Assert.Equal(PacketDirection.Inbound, records[1].Direction);
            Assert.Equal(9101, records[1].LocalPort);
            Assert.Equal(new byte[] { 9 }, records[1].Bytes);

            Assert.Empty(records[2].Bytes);
        }

        [Fact]
        public void ATruncatedFinalRecordIsDroppedAndTheRestKept()
        {
            // A proxy that crashes mid-write leaves exactly this: whole records, then a
            // partial one. The whole ones are still worth reading.
            MemoryStream stream = new MemoryStream();

            using (CaptureWriter writer = new CaptureWriter(stream))
            {
                writer.Write(DateTimeOffset.UtcNow, PacketDirection.Outbound, 1, new byte[] { 1, 2, 3, 4 });
                writer.Write(DateTimeOffset.UtcNow, PacketDirection.Outbound, 1, new byte[] { 5, 6, 7, 8 });
            }

            byte[] whole = stream.ToArray();
            byte[] cut = new byte[whole.Length - 2];
            Array.Copy(whole, cut, cut.Length);

            List<CapturedDatagram> records = new List<CapturedDatagram>(CaptureReader.Read(new MemoryStream(cut)));

            Assert.Single(records);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, records[0].Bytes);
        }

        [Fact]
        public void AFileWithoutTheMagicIsRejected()
        {
            MemoryStream notACapture = new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 });

            Assert.Throws<InvalidDataException>(() =>
                new List<CapturedDatagram>(CaptureReader.Read(notACapture)));
        }

        [Fact]
        public void AnImpossibleRecordLengthIsRejectedAsCorruption()
        {
            MemoryStream stream = new MemoryStream();
            using (CaptureWriter writer = new CaptureWriter(stream))
                writer.Write(DateTimeOffset.UtcNow, PacketDirection.Outbound, 1, new byte[] { 1 });

            byte[] bytes = stream.ToArray();

            // Length field sits at magic(8) + ticks(8) + direction(1) + port(2).
            int lengthOffset = 8 + 8 + 1 + 2;
            bytes[lengthOffset + 3] = 0x7F; // 0x7F000001: far beyond any datagram

            Assert.Throws<InvalidDataException>(() =>
                new List<CapturedDatagram>(CaptureReader.Read(new MemoryStream(bytes))));
        }
    }
}
