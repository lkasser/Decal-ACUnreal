using System;
using System.Text;
using AC.Host.World;
using AC.Protocol;

namespace AC.Host.Decoding
{
    /// <summary>
    /// Chat that arrives other than as speech, tells and the server's own lines: fellowship
    /// and allegiance-hierarchy channels, and the server's chat rooms (Turbine chat).
    /// </summary>
    /// <remarks>
    /// Layouts are ACE's writers': GameEventChannelBroadcast and GameMessageTurbineChat. The
    /// character's own line on a channel comes back with an empty sender, which is how the
    /// game client knows to write "You say".
    /// </remarks>
    internal static class ChatDecoding
    {
        /// <summary>ACE's ChatNetworkBlobType for a line spoken in a room.</summary>
        private const uint EventBinary = 1;

        /// <summary>ACE's ChatNetworkBlobType for the server's acknowledgement of the character's own line.</summary>
        private const uint ResponseBinary = 5;

        /// <summary>The channel as a bit, the sender (empty for the character's own line), then the text.</summary>
        internal static DecodeOutcome DecodeChannelBroadcast(ref SpanReader reader, WorldState world)
        {
            if (!reader.TryReadUInt32(out uint channel)) return DecodeOutcome.Malformed;
            if (!reader.TryReadString(out string sender)) return DecodeOutcome.Malformed;
            if (!reader.TryReadString(out string text)) return DecodeOutcome.Malformed;

            world.NotifyChat(new ChatMessage(ChatKind.Channel, text, sender, 0, channel));
            return DecodeOutcome.Applied;
        }

        /// <summary>
        /// A room's line: a header of sizes and constants, the room's number, the sender and
        /// the text in UTF-16 with the protocol's short lengths, then the sender's id and the
        /// room's chat type. The server's answer to the character's own line carries none of
        /// that and is only acknowledged.
        /// </summary>
        internal static DecodeOutcome DecodeTurbineChat(ref SpanReader reader, WorldState world)
        {
            if (!reader.TryReadUInt32(out _)) return DecodeOutcome.Malformed; // bytes to follow
            if (!reader.TryReadUInt32(out uint blobType)) return DecodeOutcome.Malformed;
            if (!reader.TryReadUInt32(out _)) return DecodeOutcome.Malformed; // dispatch type

            // Target and transport, each a type and an id, then a cookie and a second size.
            if (!reader.TrySkip(6 * 4)) return DecodeOutcome.Malformed;

            if (blobType == ResponseBinary)
                return DecodeOutcome.Applied;

            if (blobType != EventBinary)
                return DecodeOutcome.Ignored;

            if (!reader.TryReadUInt32(out uint room)) return DecodeOutcome.Malformed;
            if (!TryReadWideString(ref reader, out string sender)) return DecodeOutcome.Malformed;
            if (!TryReadWideString(ref reader, out string text)) return DecodeOutcome.Malformed;
            if (!reader.TryReadUInt32(out _)) return DecodeOutcome.Malformed; // size of what follows: 12
            if (!reader.TryReadUInt32(out uint senderId)) return DecodeOutcome.Malformed;
            if (!reader.TryReadUInt32(out _)) return DecodeOutcome.Malformed; // result, 0
            if (!reader.TryReadUInt32(out uint chatType)) return DecodeOutcome.Malformed;

            world.NotifyChat(new ChatMessage(ChatKind.Room, text, sender, senderId, chatType) { Room = room });
            return DecodeOutcome.Applied;
        }

        /// <summary>
        /// The protocol's short-length UTF-16 string: a byte of length under 128, or two bytes
        /// with the top bit of the first set; then that many UTF-16 characters, unpadded.
        /// </summary>
        internal static bool TryReadWideString(ref SpanReader reader, out string value)
        {
            value = null;
            if (!reader.TryReadByte(out byte first))
                return false;

            int length = first;
            if ((first & 0x80) != 0)
            {
                if (!reader.TryReadByte(out byte second))
                    return false;
                length = ((first & 0x7F) << 8) | second;
            }

            if (!reader.TryReadBytes(length * 2, out ReadOnlySpan<byte> bytes))
                return false;

            value = Encoding.Unicode.GetString(bytes);
            return true;
        }
    }
}
