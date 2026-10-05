namespace AC.Host.World
{
    public enum ChatKind
    {
        /// <summary>Speech heard nearby (HearSpeech / HearRangedSpeech).</summary>
        Speech,

        /// <summary>A tell to the player.</summary>
        Tell,

        /// <summary>A message from the server itself (ServerMessage).</summary>
        System,

        Emote,

        /// <summary>A transient status line (CommunicationTransientString).</summary>
        Transient,

        /// <summary>
        /// Combat prose the server has already written out, such as a kill message.
        /// </summary>
        Combat,

        /// <summary>
        /// A coded message from the server. <see cref="ChatMessage.ChatType"/> is an
        /// index into the client's string table rather than free text, and
        /// <see cref="ChatMessage.Text"/> is that template's parameter when it has one -
        /// a channel name, a player name. Not always an error despite the protocol's
        /// name for it: joining a chat channel arrives this way. Rendering the real
        /// sentence needs the string table from client_portal.dat.
        /// </summary>
        Coded,

        /// <summary>
        /// Fellowship or allegiance-hierarchy chat (ChannelBroadcast). <see cref="ChatMessage.ChatType"/>
        /// is the channel, as the server numbers it - 0x800 the fellowship - and an empty
        /// sender is the character's own line coming back.
        /// </summary>
        Channel,

        /// <summary>
        /// Chat in one of the server's rooms - allegiance, general, trade, LFG, roleplay,
        /// society, olthoi (Turbine chat). <see cref="ChatMessage.ChatType"/> is the room's
        /// chat type and <see cref="ChatMessage.Room"/> its number.
        /// </summary>
        Room,
    }

    public sealed class ChatMessage
    {
        public ChatMessage(ChatKind kind, string text, string senderName, uint senderId, uint chatType)
        {
            Kind = kind;
            Text = text ?? string.Empty;
            SenderName = senderName ?? string.Empty;
            SenderId = senderId;
            ChatType = chatType;
        }

        public ChatKind Kind { get; }

        public string Text { get; }

        public string SenderName { get; }

        public uint SenderId { get; }

        /// <summary>
        /// The server's chat message type (2 = speech, 3 = tell, 5 = system...), or for
        /// <see cref="ChatKind.Coded"/> the string-table id of the message template.
        /// </summary>
        public uint ChatType { get; }

        /// <summary>For <see cref="ChatKind.Room"/>, the room's number as the server gave it; otherwise 0.</summary>
        public uint Room { get; init; }

        public override string ToString()
        {
            // A coded message is shown with its template id, because the text alone is
            // only a parameter and reads as the whole message if presented bare.
            if (Kind == ChatKind.Coded)
            {
                return string.IsNullOrEmpty(Text)
                    ? $"[Coded 0x{ChatType:X4}]"
                    : $"[Coded 0x{ChatType:X4}] \"{Text}\"";
            }

            return string.IsNullOrEmpty(SenderName) ? $"[{Kind}] {Text}" : $"[{Kind}] {SenderName}: {Text}";
        }
    }
}
