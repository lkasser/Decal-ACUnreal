using System;
using AC.Protocol;

namespace AC.Host.Transport
{
    /// <summary>How a session was seen to end.</summary>
    public enum SessionEnd
    {
        /// <summary>
        /// The client closed it: a packet flagged Disconnect, or the network error that does the
        /// same. ACE drops the session at once and takes the character out of the world a few
        /// seconds later. AC:Unreal sends one when it exits, and when it gives up entering the
        /// world ("The server did not finish entering the world").
        /// </summary>
        ClientClosed,

        /// <summary>
        /// The client began logging in again, so whatever session it had before is over - the
        /// only sign there is when a client stops without saying goodbye.
        /// </summary>
        NewLogin,
    }

    /// <summary>How the first traffic a transport saw began.</summary>
    public enum SessionStart
    {
        /// <summary>
        /// A login: the client asking to log in, or either end in the handshake that follows.
        /// Everything the server says about the session is still to come.
        /// </summary>
        Login,

        /// <summary>
        /// A session already under way: neither end was beginning one, so the transport joined
        /// it in the middle - a host started while the game was connected, after the server had
        /// said who the character is and what it carries. None of that will be said again.
        /// </summary>
        UnderWay,
    }

    /// <summary>
    /// A transport that says how the traffic it relays began - a login, or a session it joined in
    /// the middle. Like the end of a session, that is in the packets' flags, which only the
    /// transport sees.
    /// </summary>
    public interface ISessionStarts
    {
        /// <summary>
        /// Raised once, on the transport's thread, for the first packet the transport sees - before
        /// any of that packet's messages, so whoever handles it acts before the first message does.
        /// </summary>
        event EventHandler<SessionStart> SessionStarted;
    }

    /// <summary>
    /// A transport that sees a session end. That is said in a packet's flags, never in a message,
    /// so no decoder sees it; the transport, which has the packets, says so.
    /// </summary>
    /// <remarks>
    /// Optional, like everything a transport may or may not be able to do. A relay and a replay
    /// both can; the host takes the character out of the world when either says so.
    /// </remarks>
    public interface ISessionBoundaries
    {
        /// <summary>Raised on the transport's thread, after the messages of the packet that ended the session.</summary>
        event EventHandler<SessionEnd> SessionEnded;
    }

    /// <summary>Reading the end of a session off a packet.</summary>
    public static class SessionBoundary
    {
        /// <summary>Whether a packet going the given way ends the session, and how.</summary>
        public static bool TryRead(AcPacket packet, PacketDirection direction, out SessionEnd end)
        {
            end = default;

            // Only the client ends a session in a packet. ACE ends one by booting the account, which
            // is a message, or by going quiet.
            if (packet == null || direction != PacketDirection.Outbound)
                return false;

            if (packet.Header.HasFlag(PacketHeaderFlags.Disconnect) || packet.Header.HasFlag(PacketHeaderFlags.NetErrorDisconnect))
            {
                end = SessionEnd.ClientClosed;
                return true;
            }

            if (packet.Header.HasFlag(PacketHeaderFlags.LoginRequest))
            {
                end = SessionEnd.NewLogin;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Header flags seen only while a session is being set up: the client's login and the
        /// handshake either end answers it with.
        /// </summary>
        private const PacketHeaderFlags HandshakeFlags =
            PacketHeaderFlags.LoginRequest
            | PacketHeaderFlags.WorldLoginRequest
            | PacketHeaderFlags.ConnectRequest
            | PacketHeaderFlags.ConnectResponse
            | PacketHeaderFlags.Referral;

        /// <summary>
        /// How traffic that begins with this packet began: a login if the packet is part of the
        /// handshake, from either end; otherwise a session already under way.
        /// </summary>
        public static SessionStart ReadStart(AcPacket packet)
        {
            if (packet == null) throw new ArgumentNullException(nameof(packet));

            return (packet.Header.Flags & HandshakeFlags) != 0 ? SessionStart.Login : SessionStart.UnderWay;
        }

        /// <summary>What the host's log says about it.</summary>
        public static string Describe(SessionEnd end) => end switch
        {
            SessionEnd.ClientClosed => "the client closed the session",
            SessionEnd.NewLogin => "the client began a new login",
            _ => "the session ended",
        };
    }
}
