using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AC.Protocol;
using AC.Proxy;

namespace AC.Host.Transport
{
    /// <summary>
    /// Replays a recorded session. The same host, decoders and plugins run over it
    /// as over a live relay, which is how decoders get developed against real
    /// traffic without a client or server present.
    /// </summary>
    public sealed class CaptureTransport : IGameTransport, ISessionBoundaries, ISessionStarts
    {
        private readonly IEnumerable<CapturedDatagram> _datagrams;
        private readonly MessageAssembler _inbound = new MessageAssembler();
        private readonly MessageAssembler _outbound = new MessageAssembler();
        private bool _started;

        public CaptureTransport(string path)
            : this(CaptureReader.Read(path), path)
        {
        }

        public CaptureTransport(IEnumerable<CapturedDatagram> datagrams, string description = "capture")
        {
            _datagrams = datagrams ?? throw new ArgumentNullException(nameof(datagrams));
            Description = description;
        }

        public string Description { get; }

        public long Datagrams { get; private set; }

        public long UnparsedDatagrams { get; private set; }

        public event EventHandler<GameMessageEventArgs> MessageReceived;

        public event EventHandler Ended;

        public event EventHandler<SessionEnd> SessionEnded;

        /// <summary>Raised for the first packet: a capture begun while the game was connected joined it mid-session.</summary>
        public event EventHandler<SessionStart> SessionStarted;

        public bool CanSend => false;

        public bool CanShowInGame => false;

        public Task ShowInGameAsync(AcMessage message, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("A capture has no client to show anything to.");

        /// <summary>
        /// Delivers the whole capture synchronously, then raises <see cref="Ended"/>.
        /// </summary>
        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            foreach (CapturedDatagram datagram in _datagrams)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Datagrams++;

                if (!AcPacket.TryParse(datagram.Bytes, out AcPacket packet))
                {
                    UnparsedDatagrams++;
                    continue;
                }

                if (!_started)
                {
                    _started = true;
                    SessionStarted?.Invoke(this, SessionBoundary.ReadStart(packet));
                }

                MessageAssembler assembler = datagram.Direction == PacketDirection.Inbound ? _inbound : _outbound;

                foreach (AcMessage message in assembler.Accept(packet))
                    MessageReceived?.Invoke(this, new GameMessageEventArgs(datagram.Direction, message));

                if (SessionBoundary.TryRead(packet, datagram.Direction, out SessionEnd end))
                    SessionEnded?.Invoke(this, end);
            }

            Ended?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public Task SendAsync(AcMessage message, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("A capture cannot be sent to.");

        /// <summary>
        /// The halves of messages still being put together when the capture ran out - all a replay
        /// has of a relay's state - for a replay of what was captured next to carry on from.
        /// </summary>
        public RelayState SaveState()
        {
            RelayState state = new RelayState();
            state.InboundPartials.AddRange(_inbound.SavePartials());
            state.OutboundPartials.AddRange(_outbound.SavePartials());
            return state;
        }

        /// <summary>Takes up the halves of messages a replay of what came before left. Before <see cref="StartAsync"/>.</summary>
        public void Resume(RelayState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));

            foreach (AcFragment fragment in state.InboundPartials)
                _inbound.TryAccept(fragment, out _);
            foreach (AcFragment fragment in state.OutboundPartials)
                _outbound.TryAccept(fragment, out _);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
