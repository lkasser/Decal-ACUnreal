using System;
using System.Threading;
using System.Threading.Tasks;
using AC.Protocol;

namespace AC.Host.Transport
{
    /// <summary>A complete game message, and which way it was going.</summary>
    public sealed class GameMessageEventArgs : EventArgs
    {
        public GameMessageEventArgs(PacketDirection direction, AcMessage message, bool fromHost = false)
        {
            Direction = direction;
            Message = message;
            FromHost = fromHost;
        }

        public PacketDirection Direction { get; }

        public AcMessage Message { get; }

        /// <summary>
        /// Whether a message going to the server is one the host sent as the client - a plugin's
        /// appraisal, a use - rather than one the client sent itself. The relay sees both go on
        /// in the client's packets; only the client's say what the player did.
        /// </summary>
        public bool FromHost { get; }
    }

    /// <summary>
    /// The host's connection to the game, whatever form it takes. Delivers messages
    /// in; will carry messages out once something can send them.
    /// </summary>
    /// <remarks>
    /// <see cref="MessageReceived"/> may be raised on any thread. The host marshals
    /// onto its game thread; the transport need not.
    /// </remarks>
    public interface IGameTransport : IAsyncDisposable
    {
        string Description { get; }

        event EventHandler<GameMessageEventArgs> MessageReceived;

        /// <summary>Raised when the transport can deliver no more (capture exhausted, relay stopped).</summary>
        event EventHandler Ended;

        Task StartAsync(CancellationToken cancellationToken = default);

        bool CanSend { get; }

        /// <summary>Sends a client-to-server message. Throws if <see cref="CanSend"/> is false.</summary>
        Task SendAsync(AcMessage message, CancellationToken cancellationToken = default);

        /// <summary>
        /// True if the transport can put a message in front of the client - which is the
        /// only way anything of ours becomes visible inside a game that cannot be drawn
        /// into.
        /// </summary>
        bool CanShowInGame { get; }

        /// <summary>
        /// Sends a server-to-client message, as though the server had sent it. Throws if
        /// <see cref="CanShowInGame"/> is false.
        /// </summary>
        Task ShowInGameAsync(AcMessage message, CancellationToken cancellationToken = default);
    }
}
