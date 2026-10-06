using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using AC.Host.Decoding;
using AC.Host.World;
using AC.Protocol;
using Xunit;

namespace AC.Host.Tests
{
    /// <summary>
    /// Chat written out as the retail chat window wrote it - the text Decal handed Virindi Tank -
    /// checked against regular expressions taken from real metas on the player's machine.
    /// </summary>
    /// <remarks>
    /// The sentences are the retail client's, which AC:Unreal kept and which its executable
    /// carries: "%s tells you", "You say", "[%s] %s says", "Your %s %s says to you". The
    /// fellowship, allegiance and room messages are ACE's writers' layouts, written out by hand:
    /// no recorded session has any.
    /// </remarks>
    public class ChatLineTests
    {
        private const uint Me = 0x50000006;
        private const uint Bob = 0x50000009;
        private const uint Guard = 0x80001234;

        private static string Line(ChatMessage message, string myName = "Tester") => ChatLines.Format(message, Me, null, myName)?.TrimEnd('\n');

        // Regular expressions from real .met files, as found in the Virindi Tank plugin folder.

        /// <summary>IBControl's remote command: another player's tell or speech, with the name link, or one's own words.</summary>
        private const string IbControlAction = "(^(\\[[A-z]+?\\] |)You|.*\\<Tell:IIDString:.+:(?<name>[^\\<]*)\\>.+\\<\\\\Tell\\>) (?<saythink>.*), \\\"#action (?<actiontext>.*)\\\"$";

        /// <summary>SocietyTasks' fellowship trigger.</summary>
        private const string FellowshipTurnIn = "^\\[Fellowship\\] (|).* (say|says), \\\"!turn in\\\"$";

        /// <summary>An NPC's tell, plain - IBControl's quest metas.</summary>
        private const string RoyalGuard = "Royal Guard tells you, \\\"Kill 15";

        /// <summary>ChaosControl's remote recall, by speech or tell, with or without the link.</summary>
        private const string RemoteRecall = "^.*(Character Y|Bob|You).* (say|says|tells you|think), \\\"!ls\\\"$";

        [Fact]
        public void APlayersTellCarriesTheNameLinkAndMatchesTheMetasThatReadIt()
        {
            string line = Line(new ChatMessage(ChatKind.Tell, "#action all on", "Bob", Bob, 3));

            Assert.Equal("<Tell:IIDString:1342177289:Bob>Bob<\\Tell> tells you, \"#action all on\"", line);
            Match match = Regex.Match(line, IbControlAction);
            Assert.True(match.Success);
            Assert.Equal("Bob", match.Groups["name"].Value);
            Assert.Equal("all on", match.Groups["actiontext"].Value);
        }

        [Fact]
        public void AnNpcsTellIsPlainAsTheQuestMetasExpect()
        {
            string line = Line(new ChatMessage(ChatKind.Tell, "Kill 15 Void Lords and I will reward you for your efforts.", "Royal Guard", Guard, 3));

            Assert.Equal("Royal Guard tells you, \"Kill 15 Void Lords and I will reward you for your efforts.\"", line);
            Assert.Matches(RoyalGuard, line);
        }

        /// <summary>The client knows its own words: speech from the character's own id reads "You say".</summary>
        [Fact]
        public void TheCharactersOwnSpeechIsYouSay()
        {
            string line = Line(new ChatMessage(ChatKind.Speech, "#action nav on", "Tester", Me, 2));

            Assert.Equal("You say, \"#action nav on\"", line);
            Assert.Matches(IbControlAction, line);
            Assert.Matches(RemoteRecall, Line(new ChatMessage(ChatKind.Speech, "!ls", "Tester", Me, 2)));
        }

        /// <summary>A tell to oneself read "You think" in the retail client.</summary>
        [Fact]
        public void ATellToOneselfIsYouThink()
        {
            Assert.Equal("You think, \"!ls\"", Line(new ChatMessage(ChatKind.Tell, "!ls", "Tester", Me, 3)));
            Assert.Matches(RemoteRecall, Line(new ChatMessage(ChatKind.Tell, "!ls", "Tester", Me, 3)));
        }

        [Fact]
        public void APlayersSpeechCarriesTheLinkAnNpcsDoesNot()
        {
            Assert.Equal("<Tell:IIDString:1342177289:Bob>Bob<\\Tell> says, \"!ls\"", Line(new ChatMessage(ChatKind.Speech, "!ls", "Bob", Bob, 2)));
            Assert.Matches(RemoteRecall, Line(new ChatMessage(ChatKind.Speech, "!ls", "Bob", Bob, 2)));
            Assert.Equal("Apparition of Borelean Strathelar says, \"NO!!!\"", Line(new ChatMessage(ChatKind.Speech, "NO!!!", "Apparition of Borelean Strathelar", Guard, 12)));
        }

        // ------------------------------------------------------------------- channels

        private static ChatMessage Broadcast(uint channel, string sender, string text)
        {
            WorldState world = new WorldState();
            ChatMessage heard = null;
            world.ChatReceived += (_, m) => heard = m;
            WireWriter w = WireWriter.GameEvent(Me, GameEvents.ChannelBroadcast).U32(channel).String16L(sender).String16L(text);
            Assert.Equal(DecodeOutcome.Applied, MessageDecoder.Apply(w.ToMessage(), PacketDirection.Inbound, world));
            return heard;
        }

        /// <summary>
        /// ChannelBroadcast (0x0147) as ACE writes it: the channel, the sender, the text - and an
        /// empty sender for the character's own line coming back.
        /// </summary>
        [Fact]
        public void FellowshipChatIsBracketedAndMatchesTheFellowshipMetas()
        {
            ChatMessage theirs = Broadcast(0x800, "Bob", "!turn in");
            Assert.Equal(ChatKind.Channel, theirs.Kind);
            Assert.Equal("[Fellowship] <Tell:IIDString:0:Bob>Bob<\\Tell> says, \"!turn in\"", Line(theirs));
            Assert.Matches(FellowshipTurnIn, Line(theirs));

            ChatMessage mine = Broadcast(0x800, string.Empty, "!turn in");
            Assert.Equal("[Fellowship] You say, \"!turn in\"", Line(mine));
            Assert.Matches(FellowshipTurnIn, Line(mine));
            Assert.Matches(IbControlAction, Line(Broadcast(0x800, string.Empty, "#action loot on")));
            Assert.Equal(19, ChatLines.Color(mine));
        }

        [Fact]
        public void AFellowsLinkCarriesTheirIdWhenTheyAreInSight()
        {
            ChatMessage theirs = Broadcast(0x800, "Bob", "hi");
            WorldState world = new WorldState();
            world.GetOrAdd(Bob, out _).Name = "Bob";

            Assert.Equal("[Fellowship] <Tell:IIDString:1342177289:Bob>Bob<\\Tell> says, \"hi\"\n",
                ChatLines.Format(theirs, Me, ChatLines.PlayerLookup(world)));
        }

        /// <summary>
        /// The allegiance's own channels are personal: a patron speaking down arrives on
        /// "vassals", a vassal speaking up on "patron", each read from where the listener stands.
        /// </summary>
        [Fact]
        public void PatronAndVassalChatSaysWhoIsSpeakingToWhom()
        {
            Assert.Equal("Your vassal <Tell:IIDString:0:Bob>Bob<\\Tell> says to you, \"hello\"", Line(Broadcast(0x2000, "Bob", "hello")));
            Assert.Equal("Your patron <Tell:IIDString:0:Bob>Bob<\\Tell> says to you, \"hello\"", Line(Broadcast(0x1000, "Bob", "hello")));
            Assert.Equal("You say to your patron, \"hello\"", Line(Broadcast(0x2000, string.Empty, "hello")));
            Assert.Equal("You say to your vassals, \"hello\"", Line(Broadcast(0x1000, string.Empty, "hello")));
            Assert.Equal("[Co-Vassals] You say, \"hello\"", Line(Broadcast(0x1000000, string.Empty, "hello")));
        }

        /// <summary>An allegiance broadcast comes back to its speaker under the speaker's own name.</summary>
        [Fact]
        public void AnAllegianceBroadcastOfOnesOwnIsYouSay()
        {
            Assert.Equal("[Allegiance] You say, \"rally\"", Line(Broadcast(0x2000000, "Tester", "rally")));
        }

        // ------------------------------------------------------------------- rooms

        /// <summary>
        /// A room's line (TurbineChat 0xF7DE, an event) as ACE writes it: sizes, the protocol's
        /// constants, the room, the sender and text in UTF-16 with short lengths, then the
        /// sender's id and the room's chat type.
        /// </summary>
        private static byte[] RoomEvent(uint room, string sender, string text, uint senderId, uint chatType)
        {
            List<byte> inner = new List<byte>();
            inner.AddRange(BitConverter.GetBytes(room));
            inner.Add((byte)sender.Length);
            inner.AddRange(Encoding.Unicode.GetBytes(sender));
            inner.Add((byte)text.Length);
            inner.AddRange(Encoding.Unicode.GetBytes(text));
            inner.AddRange(BitConverter.GetBytes(12u));
            inner.AddRange(BitConverter.GetBytes(senderId));
            inner.AddRange(BitConverter.GetBytes(0u));
            inner.AddRange(BitConverter.GetBytes(chatType));

            List<byte> payload = new List<byte>();
            foreach (uint word in new uint[] { (uint)(inner.Count + 40), 1, 1, 1, 0xB00B5, 1, 0xB00B5, 0, (uint)(inner.Count + 4) })
                payload.AddRange(BitConverter.GetBytes(word));
            payload.AddRange(inner);
            return payload.ToArray();
        }

        [Fact]
        public void ARoomsLineIsHeadedWithTheRoom()
        {
            WorldState world = new WorldState();
            List<ChatMessage> heard = new List<ChatMessage>();
            world.ChatReceived += (_, m) => heard.Add(m);

            Assert.Equal(DecodeOutcome.Applied, MessageDecoder.Apply(AcMessage.Create(Opcodes.TurbineChat, RoomEvent(2, "Bob", "hey", Bob, 2)), PacketDirection.Inbound, world));
            Assert.Equal(DecodeOutcome.Applied, MessageDecoder.Apply(AcMessage.Create(Opcodes.TurbineChat, RoomEvent(0x80001424, "Tester", "hi all", Me, 1)), PacketDirection.Inbound, world));

            Assert.Equal(2, heard.Count);
            Assert.Equal(ChatKind.Room, heard[0].Kind);
            Assert.Equal(2u, heard[0].Room);
            Assert.Equal("[General] <Tell:IIDString:1342177289:Bob>Bob<\\Tell> says, \"hey\"", Line(heard[0]));
            Assert.Equal("[Allegiance] You say, \"hi all\"", Line(heard[1]));
            Assert.Equal(18, ChatLines.Color(heard[1]));
        }

        /// <summary>The server's answer to the character's own line carries nothing to show.</summary>
        [Fact]
        public void TheRoomsAcknowledgementIsNotALine()
        {
            WorldState world = new WorldState();
            List<ChatMessage> heard = new List<ChatMessage>();
            world.ChatReceived += (_, m) => heard.Add(m);

            List<byte> payload = new List<byte>();
            foreach (uint word in new uint[] { 56, 5, 1, 1, 0xB00B5, 1, 0xB00B5, 0, 20, 7, 2, 2, 0 })
                payload.AddRange(BitConverter.GetBytes(word));

            Assert.Equal(DecodeOutcome.Applied, MessageDecoder.Apply(AcMessage.Create(Opcodes.TurbineChat, payload.ToArray()), PacketDirection.Inbound, world));
            Assert.Empty(heard);
        }

        [Fact]
        public void ASoulEmoteIsAnEmote()
        {
            WorldState world = new WorldState();
            ChatMessage heard = null;
            world.ChatReceived += (_, m) => heard = m;

            WireWriter w = new WireWriter(Opcodes.SoulEmote).U32(Bob).String16L("Bob").String16L("dances a jig.");
            Assert.Equal(DecodeOutcome.Applied, MessageDecoder.Apply(w.ToMessage(), PacketDirection.Inbound, world));

            Assert.Equal("Bob dances a jig.", Line(heard));
        }

        /// <summary>A coded message is a template's parameter, not a sentence, and is not written out as one.</summary>
        [Fact]
        public void ACodedMessageIsNoLine()
        {
            Assert.Null(ChatLines.Format(new ChatMessage(ChatKind.Coded, "General", null, 0, 0x051B), Me));
        }
    }
}
