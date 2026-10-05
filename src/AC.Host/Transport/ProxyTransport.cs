using System;
using System.Threading;
using System.Threading.Tasks;
using AC.Protocol;
using AC.Proxy;

namespace AC.Host.Transport
{
    /// <summary>
    /// The live transport: a relay between the client and the real server, with a
    /// message assembler per direction.
    /// </summary>
    public sealed class ProxyTransport : IGameTransport, ITypedCommandSource, IClientboundFilter, ISessionBoundaries, ISessionStarts
    {
        private readonly AcProxy _proxy;
        private readonly ProxyOptions _options;
        private readonly CaptureWriter _capture;
        private readonly MessageAssembler _inbound = new MessageAssembler();
        private readonly MessageAssembler _outbound = new MessageAssembler();
        private readonly object _gate = new object();

        private readonly ClientStreamRewriter _rewriter;
        private readonly ClientStreamRewriter _inboundRewriter;

        /// <param name="enableActions">
        /// Whether the host may send messages as the client. Off by default: relaying
        /// is observation, and acting on a live character is a decision the operator
        /// makes deliberately rather than gets by starting a proxy.
        /// </param>
        public ProxyTransport(ProxyOptions options, bool enableActions = false)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _proxy = new AcProxy(options);
            _proxy.PacketObserved += OnPacket;

            if (enableActions)
            {
                _rewriter = new ClientStreamRewriter();
                _proxy.Rewriter = _rewriter;

                // A line the player typed for a plugin is taken out on its way past, and
                // handed to the host instead of the server.
                _rewriter.Withhold = WithholdTyped;
                _rewriter.Withheld += OnWithheld;

                // The same machinery, pointed the other way. Putting text in the game's
                // chat window is the only way anything of ours appears inside a client
                // that cannot be drawn into, and it rides out on the server's packets
                // exactly as an action rides out on the client's. ACE numbers a bundle's
                // messages before sending them wherever they fit, so this stream is far from
                // in order, and only the server's handshake marks a new session.
                _inboundRewriter = new ClientStreamRewriter { ArrivesInOrder = false };
                _proxy.InboundRewriter = _inboundRewriter;

                // And a message of the server's can be kept from the client, the host still
                // reading it (IClientboundFilter).
                _inboundRewriter.Withheld += OnInboundWithheld;
            }

            if (!string.IsNullOrEmpty(options.CapturePath))
                _capture = new CaptureWriter(options.CapturePath);
        }

        /// <summary>The rewriter carrying our messages, or null when sending is off.</summary>
        public ClientStreamRewriter Rewriter => _rewriter;

        /// <summary>The rewriter carrying our messages to the client, or null.</summary>
        public ClientStreamRewriter InboundRewriter => _inboundRewriter;

        public string Description
            => $"relay {_options.ListenAddress}:{_options.ListenPort} -> {_options.ServerHost}:{_options.ServerPort}";

        public AcProxy Proxy => _proxy;

        public long DatagramsRelayed => _proxy.DatagramsFromClient + _proxy.DatagramsFromServer;

        public long UnparsedDatagrams => _proxy.UnparsedDatagrams;

        public event EventHandler<GameMessageEventArgs> MessageReceived;

        public event EventHandler Ended;

        public event EventHandler<SessionEnd> SessionEnded;

        public event EventHandler<SessionStart> SessionStarted;

        /// <summary>Whether a packet has been seen yet, so <see cref="SessionStarted"/> is raised once.</summary>
        private bool _started;

        public Func<string, bool> IsPluginCommand { get; set; }

        public event EventHandler<string> CommandTyped;

        /// <summary>
        /// On the relay's thread: whether a message the client is sending is a typed line that
        /// the host says is a plugin's. Anything else, and anything unreadable, goes on.
        /// </summary>
        private bool WithholdTyped(uint opcode, ReadOnlyMemory<byte> payload)
        {
            Func<string, bool> isPluginCommand = IsPluginCommand;
            return isPluginCommand != null
                && TypedLine.TryRead(opcode, payload.Span, out string text)
                && isPluginCommand(text);
        }

        public Func<uint, ReadOnlyMemory<byte>, bool> WithholdFromClient
        {
            get => _inboundRewriter?.Withhold;
            set
            {
                if (_inboundRewriter != null)
                    _inboundRewriter.Withhold = value;
            }
        }

        public bool CanWithholdFromClient => _inboundRewriter != null;

        public int MessagesWithheldFromClient => _inboundRewriter?.WithheldMessages ?? 0;

        public int SplitMessagesWithheldFromClient => _inboundRewriter?.WithheldSplitMessages ?? 0;

        /// <summary>
        /// A message of the server's kept from the client: the host reads it all the same, as
        /// though it had arrived - the capture, which records what went to the client, does not.
        /// </summary>
        private void OnInboundWithheld(uint opcode, ReadOnlyMemory<byte> payload)
        {
            lock (_gate)
                MessageReceived?.Invoke(this, new GameMessageEventArgs(PacketDirection.Inbound, AcMessage.Create(opcode, payload)));
        }

        private void OnWithheld(uint opcode, ReadOnlyMemory<byte> payload)
        {
            if (TypedLine.TryRead(opcode, payload.Span, out string text))
                CommandTyped?.Invoke(this, text);
        }

        public bool CanSend => _rewriter != null;

        public bool CanShowInGame => _inboundRewriter != null;

        /// <summary>
        /// What a relay on the same ports needs to carry this one's session on: the proxy's state
        /// and the halves of messages still being put together. Taken once this transport has
        /// been disposed, when nothing is relayed any more.
        /// </summary>
        public RelayState SaveState()
        {
            RelayState state = _proxy.SaveState();
            lock (_gate)
            {
                state.InboundPartials.AddRange(_inbound.SavePartials());
                state.OutboundPartials.AddRange(_outbound.SavePartials());
            }

            return state;
        }

        /// <summary>
        /// Carries on the session a relay on these ports was relaying when it stopped - the
        /// client it relayed for, the rewriters' numbering, its half-assembled messages. Before
        /// <see cref="StartAsync"/>.
        /// </summary>
        public void Resume(RelayState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));

            _proxy.Resume(state);
            lock (_gate)
            {
                foreach (AcFragment fragment in state.InboundPartials)
                    _inbound.TryAccept(fragment, out _);
                foreach (AcFragment fragment in state.OutboundPartials)
                    _outbound.TryAccept(fragment, out _);
            }
        }

        public Task StartAsync(CancellationToken cancellationToken = default) => _proxy.StartAsync(cancellationToken);

        /// <summary>
        /// Queues a message to go out inside the client's next packet. It leaves when
        /// the client next sends, which on a live session is within a few hundred
        /// milliseconds.
        /// </summary>
        /// <summary>
        /// Queues a message to arrive inside the server's next packet to the client. It
        /// appears in the game as though the server had sent it, which it did not - so
        /// nothing here should pretend to be anything but the host talking.
        /// </summary>
        public Task ShowInGameAsync(AcMessage message, CancellationToken cancellationToken = default)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));

            ClientStreamRewriter inbound = _inboundRewriter
                ?? throw new InvalidOperationException("This transport cannot show anything in the game.");

            inbound.Enqueue(message.Opcode, message.Payload);
            return Task.CompletedTask;
        }

        public Task SendAsync(AcMessage message, CancellationToken cancellationToken = default)
        {
            if (_rewriter == null)
                throw new NotSupportedException("This transport was created without actions enabled.");

            if (message == null)
                throw new ArgumentNullException(nameof(message));

            _rewriter.Enqueue(message.Opcode, message.Payload);
            return Task.CompletedTask;
        }

        private void OnPacket(object sender, PacketObservedEventArgs e)
        {
            _capture?.Write(e);

            if (e.Packet == null)
                return;

            // Two receive loops feed this; the assemblers are not thread-safe.
            lock (_gate)
            {
                if (!_started)
                {
                    _started = true;
                    SessionStarted?.Invoke(this, SessionBoundary.ReadStart(e.Packet));
                }

                MessageAssembler assembler = e.Direction == PacketDirection.Inbound ? _inbound : _outbound;

                foreach (AcMessage message in assembler.Accept(e.Packet))
                    MessageReceived?.Invoke(this, new GameMessageEventArgs(e.Direction, message));

                if (SessionBoundary.TryRead(e.Packet, e.Direction, out SessionEnd end))
                    SessionEnded?.Invoke(this, end);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _proxy.PacketObserved -= OnPacket;
            await _proxy.DisposeAsync().ConfigureAwait(false);
            _capture?.Dispose();
            Ended?.Invoke(this, EventArgs.Empty);
        }
    }
}
