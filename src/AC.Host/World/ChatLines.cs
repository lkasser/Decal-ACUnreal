using System;
using System.Collections.Generic;

namespace AC.Host.World
{
    /// <summary>
    /// A chat message written out as the retail client's chat window showed it - the text
    /// Decal handed its plugins, and so the text every Virindi Tank meta and Decal plugin
    /// matched its regular expressions against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The sentences are the retail client's, which AC:Unreal kept: "Name says, "...""
    /// and "You say, "..."", "Name tells you, "..."" and, for a tell to oneself,
    /// "You think, "..."", "[Fellowship] Name says, "..."", "Your vassal Name says to you,
    /// "..."" and "You say to your patron, "..."", "[General] Name says, "..."".
    /// </para>
    /// <para>
    /// A player's name was a link in the retail chat window, and Decal passed the link's
    /// markup through: "&lt;Tell:IIDString:1342177281:Name&gt;Name&lt;\Tell&gt;". Metas that
    /// answer other players' tells pick the name out of it, and metas that wait for an NPC's
    /// words match the plain name - "Royal Guard tells you" - so the markup goes round a
    /// player's name and never an NPC's. AC:Unreal draws no links, so the markup exists only
    /// here, for the plugins that expect it.
    /// </para>
    /// <para>
    /// The line ends with the newline the client wrote; a coded message is left out, since its
    /// text is only a parameter of a sentence the host cannot write without the client's
    /// string table, and a fragment matched as though it were the sentence would be worse than
    /// silence.
    /// </para>
    /// </remarks>
    public static class ChatLines
    {
        /// <summary>
        /// The line, newline included, or null for a coded message. <paramref name="playerId"/>
        /// is the character's own id, whose words read "You say".
        /// </summary>
        /// <param name="nameToId">Looks a player's id up by name, for a sender the message names only by name. May be null.</param>
        /// <param name="playerName">
        /// The character's own name: an allegiance broadcast comes back to its speaker under the
        /// speaker's name rather than none. May be null.
        /// </param>
        public static string Format(ChatMessage message, uint playerId, Func<string, uint> nameToId = null, string playerName = null)
        {
            if (message == null)
                return null;

            bool mine = playerId != 0 && message.SenderId == playerId;
            string text = message.Text;
            switch (message.Kind)
            {
                case ChatKind.Speech:
                    return (mine ? "You say" : Speaker(message.SenderName, message.SenderId) + " says") + ", \"" + text + "\"\n";

                case ChatKind.Tell:
                    return mine ? "You think, \"" + text + "\"\n" : Speaker(message.SenderName, message.SenderId) + " tells you, \"" + text + "\"\n";

                case ChatKind.Emote:
                    return message.SenderName + " " + text + "\n";

                case ChatKind.Channel:
                    return ChannelLine(message, nameToId, playerName) + "\n";

                case ChatKind.Room:
                    string room = RoomName(message.ChatType);
                    return mine || string.IsNullOrEmpty(message.SenderName)
                        ? "[" + room + "] You say, \"" + text + "\"\n"
                        : "[" + room + "] " + Speaker(message.SenderName, message.SenderId) + " says, \"" + text + "\"\n";

                case ChatKind.Coded:
                    return null;

                default:
                    return text.EndsWith("\n", StringComparison.Ordinal) ? text : text + "\n";
            }
        }

        /// <summary>
        /// The colour the client drew the line in, which is the server's chat message type for
        /// everything that has one - what Decal reported as a line's colour.
        /// </summary>
        public static int Color(ChatMessage message)
        {
            if (message == null)
                return 0;

            switch (message.Kind)
            {
                case ChatKind.Speech:
                    return message.ChatType != 0 ? unchecked((int)message.ChatType) : Speech;
                case ChatKind.Tell:
                    return message.ChatType != 0 ? unchecked((int)message.ChatType) : Tell;
                case ChatKind.Emote:
                    return Emote;
                case ChatKind.System:
                    return unchecked((int)message.ChatType);
                case ChatKind.Channel:
                    return message.ChatType == FellowChannel || message.ChatType == FellowBroadcastChannel ? Fellowship : Allegiance;
                case ChatKind.Room:
                    return message.ChatType == AllegianceRoom ? Allegiance : GlobalChat;
                default:
                    return 0;
            }
        }

        /// <summary>
        /// Looks up a player in view by name, for the channels that name a speaker without an
        /// id. 0 for a player out of sight, whose link then carries no id.
        /// </summary>
        public static Func<string, uint> PlayerLookup(IWorldView world)
        {
            if (world == null)
                return null;

            return name =>
            {
                foreach (WorldObject obj in world.Objects)
                {
                    if (IsPlayerId(obj.Id) && string.Equals(obj.Name, name, StringComparison.Ordinal))
                        return obj.Id;
                }

                return 0;
            };
        }

        /// <summary>Whether an id is a player's: the server numbers characters from 0x50000000.</summary>
        public static bool IsPlayerId(uint id) => id >= 0x50000000 && id < 0x60000000;

        /// <summary>A speaker as the retail chat window wrote one: a player's name as a link, anyone else's plain.</summary>
        public static string Speaker(string name, uint id)
            => IsPlayerId(id) ? Link(name, id) : name ?? string.Empty;

        /// <summary>The markup the retail client put round a player's name, which Decal passed through.</summary>
        public static string Link(string name, uint id)
            => "<Tell:IIDString:" + unchecked((int)id) + ":" + name + ">" + name + "<\\Tell>";

        // The server's chat message types used as colours (ACE's ChatMessageType).
        private const int Speech = 2;
        private const int Tell = 3;
        private const int Emote = 12;
        private const int Allegiance = 18;
        private const int Fellowship = 19;

        /// <summary>
        /// The first of the three types past ACE's list, which Virindi View Service's console
        /// draws as global chat - the colour of the server's rooms. Which of the three each room
        /// had is not known.
        /// </summary>
        private const int GlobalChat = 0x1B;

        // The server's channel bits (ACE's Channel).
        private const uint FellowChannel = 0x800;
        private const uint VassalsChannel = 0x1000;
        private const uint PatronChannel = 0x2000;
        private const uint MonarchChannel = 0x4000;
        private const uint CoVassalsChannel = 0x1000000;
        private const uint AllegianceBroadcastChannel = 0x2000000;
        private const uint FellowBroadcastChannel = 0x4000000;

        /// <summary>The allegiance room's chat type (ACE's ChatType); the rest follow it.</summary>
        private const uint AllegianceRoom = 1;

        /// <summary>
        /// A line on one of the channels ChannelBroadcast carries. Speaking to one's patron,
        /// vassals or monarch is personal - "Your vassal Name says to you" - and the rest read
        /// like a room. The server gives the character's own line back with no sender.
        /// </summary>
        private static string ChannelLine(ChatMessage message, Func<string, uint> nameToId, string playerName)
        {
            string text = message.Text;
            bool mine = string.IsNullOrEmpty(message.SenderName)
                || (!string.IsNullOrEmpty(playerName) && string.Equals(message.SenderName, playerName, StringComparison.Ordinal));
            string speaker = mine ? null : Link(message.SenderName, nameToId?.Invoke(message.SenderName) ?? 0);

            // A patron speaking to its vassals arrives on "vassals"; a vassal speaking up, on "patron".
            switch (message.ChatType)
            {
                case VassalsChannel:
                    return mine ? "You say to your vassals, \"" + text + "\"" : "Your patron " + speaker + " says to you, \"" + text + "\"";
                case PatronChannel:
                    return mine ? "You say to your patron, \"" + text + "\"" : "Your vassal " + speaker + " says to you, \"" + text + "\"";
                case MonarchChannel:
                    return mine ? "You say to your monarch, \"" + text + "\"" : "Your follower " + speaker + " says to you, \"" + text + "\"";
            }

            string channel = ChannelName(message.ChatType);
            return mine
                ? "[" + channel + "] You say, \"" + text + "\""
                : "[" + channel + "] " + speaker + " says, \"" + text + "\"";
        }

        private static readonly Dictionary<uint, string> ChannelNames = new Dictionary<uint, string>
        {
            [FellowChannel] = "Fellowship",
            [FellowBroadcastChannel] = "Fellowship",
            [CoVassalsChannel] = "Co-Vassals",
            [AllegianceBroadcastChannel] = "Allegiance",
            [0x8000000] = "Celestial Hand",
            [0x10000000] = "Eldrytch Web",
            [0x20000000] = "Radiant Blood",
            [0x40000000] = "Olthoi",
            [0x0001] = "Abuse",
            [0x0002] = "Admin",
            [0x0004] = "Audit",
            [0x0008] = "Advocate",
            [0x0010] = "Advocate",
            [0x0020] = "Advocate",
            [0x0040] = "QA",
            [0x0080] = "QA",
            [0x0100] = "Debug",
            [0x0200] = "Sentinel",
            [0x0400] = "Help",
        };

        private static string ChannelName(uint channel)
            => ChannelNames.TryGetValue(channel, out string name) ? name : "Channel";

        /// <summary>A room's name as the chat window headed its lines, by the room's chat type.</summary>
        public static string RoomName(uint chatType) => chatType switch
        {
            1 => "Allegiance",
            2 => "General",
            3 => "Trade",
            4 => "LFG",
            5 => "Roleplay",
            6 => "Society",
            7 => "Celestial Hand",
            8 => "Eldrytch Web",
            9 => "Radiant Blood",
            10 => "Olthoi",
            _ => "Chat",
        };
    }
}
