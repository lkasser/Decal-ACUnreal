using System;
using System.Collections.Generic;
using System.Text;
using AC.Host.Decoding;
using AC.Host.World;

namespace AC.Host.Actions
{
    /// <summary>What a typed line turned out to be, for the game client.</summary>
    public enum ClientCommandKind
    {
        /// <summary>A game action to send: <see cref="ClientCommand.Type"/> and its fields.</summary>
        Action,

        /// <summary>A whole message to send, not a game action: <see cref="ClientCommand.Type"/> is its opcode.</summary>
        Message,

        /// <summary>
        /// One of the client's chat emotes ("*dance*"): a motion to play,
        /// <see cref="ClientCommand.Motion"/>, and its words for everyone else, the SoulEmote
        /// action in <see cref="ClientCommand.Type"/> and its fields.
        /// </summary>
        Emote,

        /// <summary>One of the client's own commands that only changes the client - its windows, its settings.</summary>
        ClientOnly,

        /// <summary>
        /// One of the client's commands the server would carry out, which cannot be sent: it
        /// needs something not known yet, or this host does not build it.
        /// <see cref="ClientCommand.Reason"/> says which.
        /// </summary>
        NotSent,

        /// <summary>A line that names no command at all: "/" alone.</summary>
        Unknown,
    }

    /// <summary>A typed line, turned into what the game client would have sent for it.</summary>
    public sealed class ClientCommand
    {
        private ClientCommand(ClientCommandKind kind, uint type, byte[] fields, string reason)
        {
            Kind = kind;
            Type = type;
            Fields = fields ?? Array.Empty<byte>();
            Reason = reason ?? string.Empty;
        }

        public ClientCommandKind Kind { get; }

        /// <summary>The game action's type, or for <see cref="ClientCommandKind.Message"/> the opcode.</summary>
        public uint Type { get; }

        /// <summary>What follows the action's type, or the message's whole payload.</summary>
        public byte[] Fields { get; }

        /// <summary>Why nothing is sent, for a line that is not sent.</summary>
        public string Reason { get; }

        /// <summary>For a tell, whom it goes to - remembered for "/rt".</summary>
        public string TellTarget { get; private set; }

        /// <summary>For a chat emote, the motion command it plays; 0 for one whose command the client did not know.</summary>
        public uint Motion { get; private set; }

        /// <summary>For a chat emote, the words it sends: what everyone else reads after the character's name.</summary>
        public string EmoteText { get; private set; }

        internal static ClientCommand Action(uint type, PayloadWriter fields) => new ClientCommand(ClientCommandKind.Action, type, fields?.ToArray(), null);

        internal static ClientCommand Emote(uint motion, string text)
            => new ClientCommand(ClientCommandKind.Emote, GameActions.SoulEmote, new PayloadWriter().String(text).ToArray(), null)
            {
                Motion = motion,
                EmoteText = text,
            };

        internal static ClientCommand Message(uint opcode, byte[] payload) => new ClientCommand(ClientCommandKind.Message, opcode, payload, null);

        internal static ClientCommand Tell(string target, string text)
            => new ClientCommand(ClientCommandKind.Action, GameActions.Tell, new PayloadWriter().String(text).String(target).ToArray(), null) { TellTarget = target };

        internal static ClientCommand Not(ClientCommandKind kind, string reason) => new ClientCommand(kind, 0, null, reason);

        public override string ToString()
            => Kind switch
            {
                ClientCommandKind.Action => $"action 0x{Type:X4} ({Fields.Length} bytes)",
                ClientCommandKind.Message => $"message 0x{Type:X4} ({Fields.Length} bytes)",
                ClientCommandKind.Emote => $"emote 0x{Motion:X8} \"{EmoteText}\"",
                _ => Kind + ": " + Reason,
            };
    }

    /// <summary>What a typed line needs to know that the line itself does not say.</summary>
    public sealed class ClientCommandContext
    {
        /// <summary>The character's own id, which a chat room's line carries as its sender.</summary>
        public uint PlayerId { get; init; }

        /// <summary>Whom "/r" answers: the last to send the character a tell.</summary>
        public string LastTellFrom { get; init; } = string.Empty;

        /// <summary>Whom "/rt" tells again: the last the character told.</summary>
        public string LastTellTo { get; init; } = string.Empty;

        /// <summary>The server's numbers for its chat rooms, from SetTurbineChatChannels.</summary>
        public IReadOnlyDictionary<TurbineChannel, uint> Rooms { get; init; } = new Dictionary<TurbineChannel, uint>();

        /// <summary>A number for the room request's cookie, which the server hands back in its answer.</summary>
        public uint Cookie { get; init; } = 1;

        /// <summary>
        /// Looks a chat emote up by its pose, the word between the asterisks: the client's
        /// ChatPoseTable (<see cref="IGameData.GetChatEmote"/>). Null without the client's data,
        /// when no emote can be told from any other line.
        /// </summary>
        public Func<string, AC.Dat.ChatEmote> ChatEmotes { get; init; }

        /// <summary>The character's gender (the server's Gender, 113): 1 male, 2 female, 0 not known - for an emote's "%p".</summary>
        public int Gender { get; init; }
    }

    /// <summary>
    /// The game client's own chat-box commands, turned into the actions it sends for them -
    /// so that a line a plugin types ("/f !start", "/ls", "/t Bob, hi") does what it did when
    /// Virindi Tank's meta typed it into the retail client's chat box.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The command words are AC:Unreal's own, read from its executable, which kept the retail
    /// client's and added a few: "@" or "/" before any of them ("You may substitute a forward
    /// slash (/) for the at symbol (@)"). The actions are ACE's handlers' (GameActionType), laid
    /// out as those handlers read them.
    /// </para>
    /// <para>
    /// Plain text is speech. A line whose word is not the client's is one of the server's own
    /// commands ("@acehelp", "/corpse"), which AC:Unreal passes on in speech - "Server commands
    /// continue through Talk" - and it goes as "@" and the rest, since ACE looks for its commands
    /// only after an "@" and would say a "/" line aloud. A plugin's command never gets this far:
    /// the plugins are offered every line first.
    /// </para>
    /// <para>
    /// Commands that change only the client - its windows, its filters, its frame rate - are
    /// reported as such; nothing outside the client can reach them.
    /// </para>
    /// <para>
    /// A line that is a word between asterisks - "*dance*", "*come here*" - is one of the
    /// client's chat emotes when its ChatPoseTable has the word (ClientCommunicationSystem's
    /// pose handler in acclient.exe): the emote's motion, which the client played and sent in a
    /// MoveToState, and its words for everyone else, sent as a SoulEmote (0x01E1) with "%p" made
    /// "his", or "her" for a character whose Gender is not 1. The client sent the words whatever
    /// became of the motion, and showed "You" and its own line in its chat; the server's answer
    /// to the SoulEmote, which ACE sends the speaker too, stands for that line here. A word the
    /// table does not have is said aloud, as the client said it.
    /// </para>
    /// </remarks>
    public static class ClientCommands
    {
        private enum Verb
        {
            Say,
            Emote,
            SoulEmote,
            Tell,
            Reply,
            Retell,
            Channel,
            Room,
            Lifestone,
            Marketplace,
            House,
            Mansion,
            Hometown,
            Die,
            Afk,
            Age,
            Birth,
            PkLite,
            Motd,
            ClientOnly,
            NotBuilt,
        }

        private sealed class Entry
        {
            public Entry(Verb verb, uint channel = 0, TurbineChannel room = default)
            {
                Verb = verb;
                Channel = channel;
                Room = room;
            }

            public Verb Verb { get; }

            public uint Channel { get; }

            public TurbineChannel Room { get; }
        }

        // ACE's Channel bits for the chat channels a player speaks on.
        private const uint Fellow = 0x800;
        private const uint Vassals = 0x1000;
        private const uint Patron = 0x2000;
        private const uint Monarch = 0x4000;
        private const uint CoVassals = 0x1000000;

        /// <summary>An allegiance officer's word to the whole allegiance.</summary>
        private const uint AllegianceBroadcast = 0x2000000;

        private static readonly Dictionary<string, Entry> Words = Build();

        private static Dictionary<string, Entry> Build()
        {
            Dictionary<string, Entry> words = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

            void Add(Entry entry, params string[] names)
            {
                foreach (string name in names)
                    words[name] = entry;
            }

            Add(new Entry(Verb.Say), "s", "say");
            Add(new Entry(Verb.Emote), "e", "em", "emote", "me");
            Add(new Entry(Verb.SoulEmote), "sm", "smote", "soul", "soulemote");
            Add(new Entry(Verb.Tell), "t", "tell", "send", "whisper");
            Add(new Entry(Verb.Reply), "r", "reply");
            Add(new Entry(Verb.Retell), "rt", "retell");
            Add(new Entry(Verb.Channel, Fellow), "f", "fellow", "fellows", "fellowship", "party", "g", "group");
            Add(new Entry(Verb.Channel, Vassals), "v", "vassals");
            Add(new Entry(Verb.Channel, Patron), "p", "patron");
            Add(new Entry(Verb.Channel, Monarch), "m", "monarch");
            Add(new Entry(Verb.Channel, CoVassals), "c", "covassal", "covassals", "co-vassals");
            Add(new Entry(Verb.Channel, AllegianceBroadcast), "ab");
            Add(new Entry(Verb.Room, room: TurbineChannel.Allegiance), "a", "ca", "allegiance");
            Add(new Entry(Verb.Room, room: TurbineChannel.General), "cg", "general");
            Add(new Entry(Verb.Room, room: TurbineChannel.Trade), "ct", "trade");
            Add(new Entry(Verb.Room, room: TurbineChannel.Lfg), "clfg", "lfg");
            Add(new Entry(Verb.Room, room: TurbineChannel.Roleplay), "crp", "roleplay");
            Add(new Entry(Verb.Room, room: TurbineChannel.Society), "cs", "society", "soc");
            Add(new Entry(Verb.Room, room: TurbineChannel.Olthoi), "co", "olthoi");
            Add(new Entry(Verb.Lifestone), "ls", "lifestone", "lif");
            Add(new Entry(Verb.Marketplace), "mp", "marketplace", "mar");
            Add(new Entry(Verb.House), "hr", "hor");
            Add(new Entry(Verb.Mansion), "hom", "hoa", "ma", "mansion", "allegiancehousing", "alleg_recall");
            Add(new Entry(Verb.Hometown), "ah", "alh", "hometown", "allegiancehometown");
            Add(new Entry(Verb.Die), "die", "suicide");
            Add(new Entry(Verb.Afk), "afk");
            Add(new Entry(Verb.Age), "age");
            Add(new Entry(Verb.Birth), "birth");
            Add(new Entry(Verb.PkLite), "pkl", "pklite");
            Add(new Entry(Verb.Motd), "motd");
            Add(new Entry(Verb.ClientOnly),
                "filter", "unfilter", "messagetypes", "emotes", "lockui", "saveui", "loadui", "saveautoui", "loadautoui",
                "framerate", "loc", "day", "title", "version", "loadfile", "log", "help", "clear", "notell");
            Add(new Entry(Verb.NotBuilt),
                "squelch", "unsquelch", "join", "leave", "friends", "fillcomps", "endurance", "house");
            return words;
        }

        /// <summary>Turns a typed line into what the client would send for it.</summary>
        public static ClientCommand Parse(string line, ClientCommandContext context)
        {
            context ??= new ClientCommandContext();
            string text = (line ?? string.Empty).Trim();
            if (text.Length == 0)
                return ClientCommand.Not(ClientCommandKind.NotSent, "There is nothing to say.");

            // "*dance*": one of the client's chat emotes, when its table has the word.
            if (text.Length > 2 && text[0] == '*' && text[text.Length - 1] == '*')
            {
                if (context.ChatEmotes == null)
                    return ClientCommand.Not(ClientCommandKind.ClientOnly, $"\"{text}\" is one of the game client's emotes, and its table of them is not loaded.");

                if (context.ChatEmotes(text.Substring(1, text.Length - 2).Trim()) is AC.Dat.ChatEmote emote)
                    return ClientCommand.Emote(ChatEmoteCommands.Find(emote.Command), EmoteWords(emote, context.Gender));
            }

            if (text[0] != '/' && text[0] != '@')
                return Talk(text);

            string body = text.Substring(1);
            int space = body.IndexOf(' ');
            string word = space < 0 ? body : body.Substring(0, space);
            string rest = space < 0 ? string.Empty : body.Substring(space + 1).Trim();

            // Two-word recalls: "@house recall" (or "re"), "@house mansion_recall", "@allegiance hometown".
            if (word.Equals("house", StringComparison.OrdinalIgnoreCase)
                && (rest.Equals("recall", StringComparison.OrdinalIgnoreCase) || rest.Equals("re", StringComparison.OrdinalIgnoreCase)))
                return ClientCommand.Action(GameActions.TeleToHouse, null);
            if (word.Equals("house", StringComparison.OrdinalIgnoreCase) && rest.Equals("mansion_recall", StringComparison.OrdinalIgnoreCase))
                return ClientCommand.Action(GameActions.TeleToMansion, null);
            if (word.Equals("allegiance", StringComparison.OrdinalIgnoreCase) && rest.Equals("hometown", StringComparison.OrdinalIgnoreCase))
                return ClientCommand.Action(GameActions.RecallAllegianceHometown, null);

            if (word.Length == 0)
                return ClientCommand.Not(ClientCommandKind.Unknown, $"\"{text}\" names no command.");

            // Not the client's: one of the server's own commands, which it looks for in speech
            // beginning "@" - where the client sends it, "/" or "@" as typed. ACE answers one it
            // does not have with "Unknown command:"; a "/" would have it said aloud instead.
            if (!Words.TryGetValue(word, out Entry entry))
                return Talk("@" + body);

            switch (entry.Verb)
            {
                case Verb.Say:
                    return rest.Length == 0 ? Usage(word, "<text>") : Talk(rest);

                case Verb.Emote:
                    return rest.Length == 0 ? Usage(word, "<text>") : ClientCommand.Action(GameActions.Emote, new PayloadWriter().String(rest));

                case Verb.SoulEmote:
                    return rest.Length == 0 ? Usage(word, "<text>") : ClientCommand.Action(GameActions.SoulEmote, new PayloadWriter().String(rest));

                case Verb.Tell:
                {
                    int comma = rest.IndexOf(',');
                    if (comma <= 0)
                        return Usage(word, "<name>, <text>");

                    string target = rest.Substring(0, comma).Trim();
                    string said = rest.Substring(comma + 1).Trim();
                    return target.Length == 0 || said.Length == 0 ? Usage(word, "<name>, <text>") : ClientCommand.Tell(target, said);
                }

                case Verb.Reply:
                    if (rest.Length == 0)
                        return Usage(word, "<text>");
                    return string.IsNullOrEmpty(context.LastTellFrom)
                        ? ClientCommand.Not(ClientCommandKind.NotSent, "Nobody has sent you a tell to reply to.")
                        : ClientCommand.Tell(context.LastTellFrom, rest);

                case Verb.Retell:
                    if (rest.Length == 0)
                        return Usage(word, "<text>");
                    return string.IsNullOrEmpty(context.LastTellTo)
                        ? ClientCommand.Not(ClientCommandKind.NotSent, "You have not sent anyone a tell yet.")
                        : ClientCommand.Tell(context.LastTellTo, rest);

                case Verb.Channel:
                    return rest.Length == 0 ? Usage(word, "<text>") : ClientCommand.Action(GameActions.ChatChannel, new PayloadWriter().UInt32(entry.Channel).String(rest));

                case Verb.Room:
                    return rest.Length == 0 ? Usage(word, "<text>") : Room(entry.Room, rest, context);

                case Verb.Lifestone:
                    return ClientCommand.Action(GameActions.TeleToLifestone, null);

                case Verb.Marketplace:
                    return ClientCommand.Action(GameActions.TeleToMarketPlace, null);

                case Verb.House:
                    return ClientCommand.Action(GameActions.TeleToHouse, null);

                case Verb.Mansion:
                    return ClientCommand.Action(GameActions.TeleToMansion, null);

                case Verb.Hometown:
                    return ClientCommand.Action(GameActions.RecallAllegianceHometown, null);

                case Verb.Die:
                    return ClientCommand.Action(GameActions.Suicide, null);

                case Verb.Afk:
                    // ACE's handler reads the switch as a word; a message, when given, is its own action first.
                    return rest.Length == 0
                        ? ClientCommand.Action(GameActions.SetAfkMode, new PayloadWriter().UInt32(1))
                        : ClientCommand.Not(ClientCommandKind.NotSent, "An away message is two actions; use \"/afk\" alone.");

                // Both read a name first, empty for one's own.
                case Verb.Age:
                    return ClientCommand.Action(GameActions.QueryAge, new PayloadWriter().String(rest));

                case Verb.Birth:
                    return ClientCommand.Action(GameActions.QueryBirth, new PayloadWriter().String(rest));

                case Verb.PkLite:
                    return ClientCommand.Action(GameActions.EnterPkLite, null);

                // Asked for alone; setting or clearing the allegiance's message is a monarch's business.
                case Verb.Motd:
                    return rest.Length == 0
                        ? ClientCommand.Action(GameActions.QueryMotd, null)
                        : ClientCommand.Not(ClientCommandKind.NotSent, $"\"/{word} {rest}\" is one of the game client's commands this host cannot send yet.");

                case Verb.ClientOnly:
                    return ClientCommand.Not(ClientCommandKind.ClientOnly, $"\"/{word}\" changes only the game client itself, which nothing outside it can reach.");

                default:
                    return ClientCommand.Not(ClientCommandKind.NotSent, $"\"/{word}\" is one of the game client's commands this host cannot send yet.");
            }
        }

        private static ClientCommand Talk(string text) => ClientCommand.Action(GameActions.Talk, new PayloadWriter().String(text));

        /// <summary>An emote's words for everyone else, "%p" made the character's: "his", unless its Gender says otherwise.</summary>
        private static string EmoteWords(AC.Dat.ChatEmote emote, int gender)
            => emote.Others.Replace("%p", gender == 0 || gender == 1 ? "his" : "her", StringComparison.Ordinal);

        private static ClientCommand Usage(string word, string arguments)
            => ClientCommand.Not(ClientCommandKind.NotSent, $"Usage: /{word} {arguments}");

        /// <summary>
        /// A line for one of the server's rooms (Turbine chat): a request to send to the room by
        /// its number, which the server gave at login. Laid out as ACE's TurbineChatHandler reads
        /// it: sizes and the constants the protocol carries, a cookie the server hands back, the
        /// room, the text as UTF-16 with a short length, the sender and the room's chat type.
        /// </summary>
        private static ClientCommand Room(TurbineChannel room, string text, ClientCommandContext context)
        {
            if (!context.Rooms.TryGetValue(room, out uint roomId) || roomId == 0)
            {
                return ClientCommand.Not(ClientCommandKind.NotSent, room == TurbineChannel.Allegiance
                    ? "You are not in an allegiance with a chat room, or the server has not said which it is."
                    : $"The server has not named its {ChatLines.RoomName(ChatType(room))} room.");
            }

            PayloadWriter body = new PayloadWriter()
                .UInt32(context.Cookie)
                .UInt32(2)
                .UInt32(2)
                .UInt32(roomId);
            WideString(body, text);
            body.UInt32(12).UInt32(context.PlayerId).UInt32(0).UInt32(ChatType(room));
            byte[] inner = body.ToArray();

            PayloadWriter message = new PayloadWriter();
            message.UInt32((uint)(inner.Length + 32))           // bytes to follow
                   .UInt32(RequestBinary)
                   .UInt32(SendToRoomById)
                   .UInt32(1).UInt32(TurbineChatId)              // target
                   .UInt32(1).UInt32(TurbineChatId)              // transport
                   .UInt32(0)                                    // cookie
                   .UInt32((uint)inner.Length);                  // bytes to follow
            byte[] header = message.ToArray();

            byte[] payload = new byte[header.Length + inner.Length];
            header.CopyTo(payload, 0);
            inner.CopyTo(payload, header.Length);
            return ClientCommand.Message(Opcodes.TurbineChat, payload);
        }

        /// <summary>ACE's ChatNetworkBlobType for a line the client sends.</summary>
        private const uint RequestBinary = 3;

        /// <summary>ACE's ChatNetworkBlobDispatchType for a room named by its number.</summary>
        private const uint SendToRoomById = 2;

        /// <summary>The id the protocol gives the chat service, as ACE writes it (0x000B00B5).</summary>
        private const uint TurbineChatId = 721077;

        /// <summary>A room's chat type, ACE's ChatType: one more than its place in the server's list, with Olthoi last.</summary>
        private static uint ChatType(TurbineChannel room) => room switch
        {
            TurbineChannel.Allegiance => 1,
            TurbineChannel.General => 2,
            TurbineChannel.Trade => 3,
            TurbineChannel.Lfg => 4,
            TurbineChannel.Roleplay => 5,
            TurbineChannel.Society => 6,
            TurbineChannel.SocietyCelestialHand => 7,
            TurbineChannel.SocietyEldrytchWeb => 8,
            TurbineChannel.SocietyRadiantBlood => 9,
            TurbineChannel.Olthoi => 10,
            _ => 2,
        };

        /// <summary>The protocol's short-length UTF-16 string, as ACE's handler reads one.</summary>
        private static void WideString(PayloadWriter writer, string text)
        {
            byte[] bytes = Encoding.Unicode.GetBytes(text);
            int length = text.Length;
            if (length < 0x80)
            {
                writer.Byte((byte)length);
            }
            else
            {
                writer.Byte((byte)(0x80 | (length >> 8)));
                writer.Byte((byte)(length & 0xFF));
            }

            writer.Bytes(bytes);
        }
    }
}
