using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AC.Protocol;

namespace AC.Proxy
{
    /// <summary>One relayed datagram, already forwarded by the time this is raised.</summary>
    public sealed class PacketObservedEventArgs : EventArgs
    {
        public PacketObservedEventArgs(
            PacketDirection direction,
            int localPort,
            ReadOnlyMemory<byte> datagram,
            AcPacket packet)
        {
            Direction = direction;
            LocalPort = localPort;
            Datagram = datagram;
            Packet = packet;
            Timestamp = DateTimeOffset.UtcNow;
        }

        public PacketDirection Direction { get; }

        /// <summary>Which relayed port this arrived on.</summary>
        public int LocalPort { get; }

        /// <summary>The bytes exactly as they were relayed.</summary>
        public ReadOnlyMemory<byte> Datagram { get; }

        /// <summary>
        /// The parsed packet, or null when the bytes did not parse. Null is not a
        /// failure of the relay - the datagram was still forwarded untouched.
        /// </summary>
        public AcPacket Packet { get; }

        public DateTimeOffset Timestamp { get; }

        public bool Parsed => Packet != null;
    }

    /// <summary>
    /// A UDP relay between a game client and a real server, which observes the traffic
    /// as it passes.
    /// </summary>
    /// <remarks>
    /// The client is configured to connect to this proxy instead of the server, so the
    /// proxy sees the whole session without the client cooperating in any way. That is
    /// the point: AC:Unreal exposes no plugin API, but it does speak the protocol.
    ///
    /// Datagrams are forwarded byte-for-byte, always, before anything tries to
    /// interpret them. A parser bug, an unknown message, or a protocol change must
    /// degrade observation, never the game: the relay path does not depend on the
    /// parse succeeding.
    ///
    /// Each relayed port is an independent channel with a fixed upstream port, and
    /// replies go back out the socket they arrived on. That is what makes the
    /// login-then-world port handoff work without inspecting a single byte.
    /// </remarks>
    public sealed class AcProxy : IAsyncDisposable
    {
        private readonly ProxyOptions _options;
        private readonly List<PortRelay> _relays = new List<PortRelay>();
        private readonly Dictionary<int, IPEndPoint> _resumedClients = new Dictionary<int, IPEndPoint>();
        private readonly CancellationTokenSource _stopping = new CancellationTokenSource();
        private IPAddress[] _serverAddresses = Array.Empty<IPAddress>();
        private Task _running;

        public AcProxy(ProxyOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _options.Validate();
        }

        /// <summary>
        /// Raised for each relayed datagram, after it has been forwarded. Handlers run
        /// on the receive path, so a slow handler delays the game - do the work
        /// elsewhere.
        /// </summary>
        public event EventHandler<PacketObservedEventArgs> PacketObserved;

        /// <summary>
        /// Set before <see cref="StartAsync"/> to let messages be sent as the client.
        /// Null - the default - makes the proxy strictly an observer, which is the
        /// right posture until something actually needs to act.
        /// </summary>
        public ClientStreamRewriter Rewriter { get; set; }

        /// <summary>
        /// Weaves messages into the stream going to the client, the same way
        /// <see cref="Rewriter"/> does for the stream going to the server. Used to put
        /// text in the game's own chat window, which is the only way anything can appear
        /// inside a client that cannot be drawn into.
        /// </summary>
        public ClientStreamRewriter InboundRewriter { get; set; }

        public long DatagramsFromClient { get; private set; }

        public long DatagramsFromServer { get; private set; }

        /// <summary>Datagrams relayed that did not parse as AC packets.</summary>
        public long UnparsedDatagrams { get; private set; }

        /// <summary>
        /// Datagrams that arrived from one of this proxy's own listening endpoints,
        /// which can only mean it is talking to itself. Always zero once
        /// <see cref="StartAsync"/> has rejected an overlapping configuration; kept as
        /// a second line of defence, since a silent loop looks exactly like a working
        /// session to anyone reading a packet log.
        /// </summary>
        public long LoopedBackDatagrams
        {
            get
            {
                long total = 0;
                foreach (PortRelay relay in _relays)
                    total += relay.LoopedBack;
                return total;
            }
        }

        /// <summary>The local endpoints now listening, once started.</summary>
        public IReadOnlyList<IPEndPoint> ListeningOn
        {
            get
            {
                List<IPEndPoint> endpoints = new List<IPEndPoint>(_relays.Count);
                foreach (PortRelay relay in _relays)
                    endpoints.Add(relay.LocalEndPoint);
                return endpoints;
            }
        }

        /// <summary>The client each relayed port has heard from, by local port: none until one has spoken.</summary>
        public IReadOnlyDictionary<int, IPEndPoint> Clients
        {
            get
            {
                Dictionary<int, IPEndPoint> clients = new Dictionary<int, IPEndPoint>();
                foreach (PortRelay relay in _relays)
                {
                    IPEndPoint client = relay.Client;
                    if (client != null)
                        clients[relay.Port] = client;
                }

                return clients;
            }
        }

        /// <summary>
        /// What another relay on the same ports needs to carry this one's session on: whom each
        /// port relays for, and both rewriters' numbering. Taken once the proxy has stopped, when
        /// nothing is being relayed; the caller adds the halves of messages it was assembling.
        /// </summary>
        public RelayState SaveState()
        {
            RelayState state = new RelayState
            {
                Outbound = Rewriter?.SaveState(),
                Inbound = InboundRewriter?.SaveState(),
            };

            foreach (KeyValuePair<int, IPEndPoint> client in Clients)
                state.Clients[client.Key] = client.Value;

            return state;
        }

        /// <summary>
        /// Carries on a session another relay on these ports was relaying when it stopped. Called
        /// before <see cref="StartAsync"/>, after the rewriters are set.
        /// </summary>
        /// <remarks>
        /// The client is known before it speaks, so the server's first datagram - which after a
        /// restart may well come first - goes to it rather than being taken for the client's own
        /// and sent back to the server. A new client from another endpoint replaces it, as ever.
        /// Each rewriter takes up its numbering; a new login starts that over at its handshake.
        /// </remarks>
        public void Resume(RelayState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (_running != null) throw new InvalidOperationException("A relay resumes a session before it starts.");

            _resumedClients.Clear();
            foreach (KeyValuePair<int, IPEndPoint> client in state.Clients)
                _resumedClients[client.Key] = client.Value;

            if (state.Outbound != null)
                Rewriter?.RestoreState(state.Outbound);
            if (state.Inbound != null)
                InboundRewriter?.RestoreState(state.Inbound);
        }

        /// <summary>
        /// Resolves the server, binds every local port, and begins relaying. Returns
        /// once listening; the returned task from <see cref="WaitAsync"/> completes when
        /// the proxy stops.
        /// </summary>
        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (_running != null)
                throw new InvalidOperationException("The proxy is already started.");

            _serverAddresses = await Dns.GetHostAddressesAsync(_options.ServerHost, cancellationToken)
                .ConfigureAwait(false);

            if (_serverAddresses.Length == 0)
                throw new InvalidOperationException($"'{_options.ServerHost}' did not resolve to an address.");

            ThrowIfSelfLoop();

            // Bind everything before relaying anything, so a port conflict fails the
            // start rather than half-opening the proxy.
            for (int offset = 0; offset < _options.PortCount; offset++)
            {
                _resumedClients.TryGetValue(_options.ListenPort + offset, out IPEndPoint resumed);

                PortRelay relay = new PortRelay(
                    new IPEndPoint(_options.ListenAddress, _options.ListenPort + offset),
                    new IPEndPoint(_serverAddresses[0], _options.ServerPort + offset),
                    _serverAddresses,
                    _options.ServerPort,
                    _options.PortCount,
                    OnDatagram,
                    Rewriter,
                    InboundRewriter,
                    resumed);

                try
                {
                    relay.Bind();
                }
                catch
                {
                    foreach (PortRelay bound in _relays)
                        bound.Dispose();
                    _relays.Clear();
                    relay.Dispose();
                    throw;
                }

                _relays.Add(relay);
            }

            List<Task> loops = new List<Task>(_relays.Count);
            foreach (PortRelay relay in _relays)
                loops.Add(relay.RunAsync(_stopping.Token));

            _running = Task.WhenAll(loops);
        }

        /// <summary>Completes when the proxy has stopped.</summary>
        public Task WaitAsync() => _running ?? Task.CompletedTask;

        public async ValueTask DisposeAsync()
        {
            if (!_stopping.IsCancellationRequested)
                _stopping.Cancel();

            foreach (PortRelay relay in _relays)
                relay.Dispose();

            if (_running != null)
            {
                try
                {
                    await _running.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected: stopping cancels the receive loops.
                }
            }

            _stopping.Dispose();
        }

        /// <summary>
        /// Refuses a configuration in which the relay would forward to a port it is
        /// itself listening on.
        /// </summary>
        /// <remarks>
        /// Such a proxy appears to work: every datagram comes straight back, is
        /// classified as a server reply because it really does arrive from the
        /// upstream address and port, and is handed to the client. The client sees its
        /// own packets echoed, the real server never receives anything, and the only
        /// symptom is that the game will not log in. Cheaper to make impossible than
        /// to diagnose.
        /// </remarks>
        private void ThrowIfSelfLoop()
        {
            if (!_options.PortRangesOverlap)
                return;

            // Overlapping ports only loop when both sides are the same host. A
            // wildcard listen address covers every local address, the server included.
            bool sameHost = _options.ListenAddress.Equals(IPAddress.Any)
                            || _options.ListenAddress.Equals(IPAddress.IPv6Any);

            if (!sameHost)
            {
                foreach (IPAddress address in _serverAddresses)
                {
                    if (_options.ListenAddress.Equals(address))
                    {
                        sameHost = true;
                        break;
                    }
                }
            }

            if (!sameHost)
                return;

            int listenLast = _options.ListenPort + _options.PortCount - 1;
            int serverLast = _options.ServerPort + _options.PortCount - 1;

            throw new InvalidOperationException(
                $"The proxy would forward to itself: it listens on {_options.ListenAddress}:{_options.ListenPort}-{listenLast} "
                + $"and forwards to {_options.ServerHost}:{_options.ServerPort}-{serverLast}, which is the same host and overlapping ports. "
                + "Every datagram would be echoed back to the client and the real server would receive nothing. "
                + "Give the proxy a listen port range that does not overlap the server's, or move the server.");
        }

        private void OnDatagram(PacketDirection direction, int localPort, ReadOnlyMemory<byte> datagram)
        {
            if (direction == PacketDirection.Outbound)
                DatagramsFromClient++;
            else
                DatagramsFromServer++;

            AcPacket packet = null;
            if (!AcPacket.TryParse(datagram.Span, out packet))
                UnparsedDatagrams++;

            EventHandler<PacketObservedEventArgs> handler = PacketObserved;
            if (handler == null)
                return;

            // An observer must not be able to take the relay down with it.
            try
            {
                handler(this, new PacketObservedEventArgs(direction, localPort, datagram, packet));
            }
            catch (Exception)
            {
                // Observation is best-effort; relaying is not.
            }
        }

        /// <summary>
        /// One local port bridged to one upstream port.
        /// </summary>
        private sealed class PortRelay : IDisposable
        {
            private readonly IPEndPoint _localEndPoint;
            private readonly IPEndPoint _upstream;
            private readonly IPAddress[] _serverAddresses;
            private readonly int _serverPortFirst;
            private readonly int _serverPortCount;
            private readonly Action<PacketDirection, int, ReadOnlyMemory<byte>> _onDatagram;
            private readonly ClientStreamRewriter _rewriter;
            private readonly ClientStreamRewriter _inboundRewriter;

            private Socket _socket;
            private IPEndPoint _client;

            /// <summary>Datagrams seen arriving from this relay's own endpoint.</summary>
            internal long LoopedBack;

            internal PortRelay(
                IPEndPoint localEndPoint,
                IPEndPoint upstream,
                IPAddress[] serverAddresses,
                int serverPortFirst,
                int serverPortCount,
                Action<PacketDirection, int, ReadOnlyMemory<byte>> onDatagram,
                ClientStreamRewriter rewriter,
                ClientStreamRewriter inboundRewriter,
                IPEndPoint client)
            {
                _localEndPoint = localEndPoint;
                _upstream = upstream;
                _serverAddresses = serverAddresses;
                _serverPortFirst = serverPortFirst;
                _serverPortCount = serverPortCount;
                _onDatagram = onDatagram;
                _rewriter = rewriter;
                _inboundRewriter = inboundRewriter;
                _client = client;
            }

            internal IPEndPoint LocalEndPoint => (IPEndPoint)(_socket?.LocalEndPoint ?? _localEndPoint);

            /// <summary>The configured local port, whatever the socket says.</summary>
            internal int Port => _localEndPoint.Port;

            /// <summary>The client last heard from - or resumed - on this port, or null.</summary>
            internal IPEndPoint Client => Volatile.Read(ref _client);

            internal void Bind()
            {
                _socket = new Socket(_localEndPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);

                // An ICMP port-unreachable from a peer that is not listening would
                // otherwise surface as a ConnectionReset on the next receive and kill
                // the loop. For a relay, that is noise.
                if (OperatingSystem.IsWindows())
                    _socket.IOControl(unchecked((int)0x9800000C), new byte[] { 0, 0, 0, 0 }, null);

                try
                {
                    _socket.Bind(_localEndPoint);
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
                {
                    // Overwhelmingly this is a second copy of the proxy, which is easy
                    // to do by accident and produces an unreadable stack trace
                    // otherwise.
                    throw new InvalidOperationException(
                        $"{_localEndPoint} is already in use - most likely another copy of the proxy is running. "
                        + "Stop it, or pass a different --listen-port.", ex);
                }
            }

            internal async Task RunAsync(CancellationToken cancellationToken)
            {
                byte[] buffer = new byte[AcPacket.MaxDatagramSize];
                EndPoint from = new IPEndPoint(
                    _localEndPoint.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any,
                    0);

                while (!cancellationToken.IsCancellationRequested)
                {
                    SocketReceiveFromResult received;

                    try
                    {
                        received = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, from, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                    catch (SocketException)
                    {
                        // A transient socket error must not end the session; the next
                        // receive usually succeeds.
                        continue;
                    }

                    if (received.ReceivedBytes <= 0)
                        continue;

                    IPEndPoint remote = (IPEndPoint)received.RemoteEndPoint;

                    // Our own endpoint on the far side means the relay is talking to
                    // itself. Forwarding it would bounce the datagram forever.
                    if (remote.Equals(_localEndPoint))
                    {
                        LoopedBack++;
                        continue;
                    }

                    byte[] datagram = new byte[received.ReceivedBytes];
                    Array.Copy(buffer, datagram, received.ReceivedBytes);

                    PacketDirection direction = Classify(remote);
                    IPEndPoint destination;

                    if (direction == PacketDirection.Outbound)
                    {
                        _client = remote;
                        destination = _upstream;

                        // Weave in anything queued to be sent as the client. What goes
                        // on the wire is what gets observed, so a capture stays a
                        // faithful record of the session the server actually saw.
                        ClientStreamRewriter rewriter = _rewriter;
                        if (rewriter != null)
                            datagram = rewriter.Rewrite(datagram);
                    }
                    else
                    {
                        destination = _client;

                        ClientStreamRewriter inbound = _inboundRewriter;
                        if (inbound != null)
                            datagram = inbound.Rewrite(datagram);
                    }

                    // Forward first. Interpretation comes after the game has its bytes.
                    if (destination != null)
                    {
                        try
                        {
                            await _socket.SendToAsync(datagram, SocketFlags.None, destination, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                        catch (ObjectDisposedException)
                        {
                            return;
                        }
                        catch (SocketException)
                        {
                            // Dropping a datagram is what UDP allows; the peers recover.
                        }
                    }

                    _onDatagram(direction, _localEndPoint.Port, datagram);
                }
            }

            /// <summary>
            /// Decides whether a datagram came from the server or the client.
            /// </summary>
            /// <remarks>
            /// Matching the known client endpoint first is what makes this work when the
            /// server runs on the same machine, where an address comparison alone cannot
            /// tell the two apart. Until a client has spoken there is nothing a server
            /// could be replying to, so the first datagram is the client's whatever port
            /// it came from - which also covers a client whose ephemeral port happens to
            /// fall inside the server's range. A relay carrying on another's session
            /// (<see cref="Resume"/>) knows its client before it speaks, and so is past
            /// that point from the start. After that, the server is recognized by
            /// address and by its port falling in the relayed range: it may reply from a
            /// port other than the one addressed, so a single-port comparison would
            /// misclassify it as a second client.
            /// </remarks>
            private PacketDirection Classify(IPEndPoint remote)
            {
                if (_client == null)
                    return PacketDirection.Outbound;

                if (remote.Equals(_client))
                    return PacketDirection.Outbound;

                if (remote.Port >= _serverPortFirst && remote.Port < _serverPortFirst + _serverPortCount)
                {
                    foreach (IPAddress address in _serverAddresses)
                    {
                        if (remote.Address.Equals(address))
                            return PacketDirection.Inbound;
                    }
                }

                return PacketDirection.Outbound;
            }

            public void Dispose()
            {
                try
                {
                    _socket?.Dispose();
                }
                catch (Exception)
                {
                    // Nothing useful to do while tearing down.
                }
            }
        }
    }
}
