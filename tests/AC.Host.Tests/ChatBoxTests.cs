using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AC.Host.Actions;
using AC.Host.Decoding;
using AC.Host.Plugins;
using AC.Host.Transport;
using AC.Host.World;
using AC.Protocol;
using Xunit;

namespace AC.Host.Tests
{
    /// <summary>
    /// The host's chat box: lines run as though typed reach other plugins and then the game
    /// client's own commands, sent as the client would send them; and lines the player types
    /// for a plugin are kept from the server and given to it.
    /// </summary>
    /// <remarks>
    /// The command words are AC:Unreal's own, read from its executable. The actions are ACE's
    /// handlers' read order, written out here field by field; no recorded session has the
    /// character chatting.
    /// </remarks>
    public class ChatBoxTests
    {
        private const uint Me = 0x50000006;

        private static string Hex(byte[] bytes) => Convert.ToHexString(bytes);

        private static string Text(string s)
        {
            byte[] text = Encoding.Latin1.GetBytes(s);
            List<byte> field = new List<byte>(BitConverter.GetBytes((ushort)text.Length));
            field.AddRange(text);
            while (field.Count % 4 != 0)
                field.Add(0);
            return Convert.ToHexString(field.ToArray());
        }

        private static ClientCommand Parse(string line, ClientCommandContext context = null) => ClientCommands.Parse(line, context);

        // ------------------------------------------------------------------- the client's commands

        [Fact]
        public void PlainTextAndSayAreSpeech()
        {
            ClientCommand plain = Parse("hello there");
            Assert.Equal(ClientCommandKind.Action, plain.Kind);
            Assert.Equal(GameActions.Talk, plain.Type);
            Assert.Equal(Text("hello there"), Hex(plain.Fields));

            Assert.Equal(Text("BuffCaster online!"), Hex(Parse("/s BuffCaster online!").Fields));
            Assert.Equal(Text("hi"), Hex(Parse("@say hi").Fields));
        }

        /// <summary>A tell is the message first, then the name - ACE's GameActionTell - typed "name, text".</summary>
        [Fact]
        public void ATellIsTheMessageThenTheName()
        {
            ClientCommand tell = Parse("/t Bob Smith, meet at the lifestone");
            Assert.Equal(GameActions.Tell, tell.Type);
            Assert.Equal(Text("meet at the lifestone") + Text("Bob Smith"), Hex(tell.Fields));
            Assert.Equal("Bob Smith", tell.TellTarget);

            Assert.Equal(GameActions.Tell, Parse("@tell Bob, hi").Type);
            Assert.Equal(ClientCommandKind.NotSent, Parse("/t Bob hi").Kind);
        }

        [Fact]
        public void ReplyAndRetellNeedSomeoneToAnswer()
        {
            Assert.Equal(ClientCommandKind.NotSent, Parse("/r thanks").Kind);
            Assert.Equal(ClientCommandKind.NotSent, Parse("/rt again").Kind);

            ClientCommandContext context = new ClientCommandContext { LastTellFrom = "Alice", LastTellTo = "Bob" };
            Assert.Equal(Text("thanks") + Text("Alice"), Hex(Parse("/r thanks", context).Fields));
            Assert.Equal(Text("again") + Text("Bob"), Hex(Parse("/rt again", context).Fields));
        }

        /// <summary>ChatChannel (0x0147): the channel as ACE numbers it, then the text.</summary>
        [Theory]
        [InlineData("/f !turn in", 0x800u, "!turn in")]
        [InlineData("@fellowship go", 0x800u, "go")]
        [InlineData("/g ready", 0x800u, "ready")]
        [InlineData("/v listen up", 0x1000u, "listen up")]
        [InlineData("/p hello patron", 0x2000u, "hello patron")]
        [InlineData("/m my liege", 0x4000u, "my liege")]
        [InlineData("/c hi all", 0x1000000u, "hi all")]
        [InlineData("/ab rally", 0x2000000u, "rally")]
        public void ChannelChatIsTheChannelThenTheText(string line, uint channel, string text)
        {
            ClientCommand command = Parse(line);
            Assert.Equal(GameActions.ChatChannel, command.Type);
            Assert.Equal(Convert.ToHexString(BitConverter.GetBytes(channel)) + Text(text), Hex(command.Fields));
        }

        [Theory]
        [InlineData("/ls", 0x0063u)]
        [InlineData("@lifestone", 0x0063u)]
        [InlineData("/mp", 0x028Du)]
        [InlineData("/hr", 0x0262u)]
        [InlineData("/house recall", 0x0262u)]
        [InlineData("/hom", 0x0278u)]
        [InlineData("/hoa", 0x0278u)]
        [InlineData("/house mansion_recall", 0x0278u)]
        [InlineData("/ah", 0x02ABu)]
        [InlineData("/allegiance hometown", 0x02ABu)]
        [InlineData("/die", 0x0279u)]
        [InlineData("/pklite", 0x028Fu)]
        [InlineData("/motd", 0x0255u)]
        public void RecallsAndTheLikeAreBareActions(string line, uint action)
        {
            ClientCommand command = Parse(line);
            Assert.Equal(ClientCommandKind.Action, command.Kind);
            Assert.Equal(action, command.Type);
            Assert.Empty(command.Fields);
        }

        [Fact]
        public void EmotesAreTheirText()
        {
            Assert.Equal(GameActions.Emote, Parse("/e waves").Type);
            Assert.Equal(Text("waves"), Hex(Parse("/me waves").Fields));
            Assert.Equal(GameActions.SoulEmote, Parse("/sm bows").Type);
        }

        /// <summary>
        /// A word the client does not have is the server's: ACE looks for its commands after an
        /// "@", and would say a "/" line aloud, so it goes as "@" and the rest.
        /// </summary>
        [Fact]
        public void TheServersOwnCommandsGoAsSpeechAfterAnAt()
        {
            Assert.Equal(Text("@acehelp"), Hex(Parse("@acehelp").Fields));
            Assert.Equal(Text("@corpse"), Hex(Parse("/corpse").Fields));
            Assert.Equal(Text("@permit add Bob"), Hex(Parse("/permit add Bob").Fields));
            Assert.Equal(ClientCommandKind.Unknown, Parse("/").Kind);
        }

        [Theory]
        [InlineData("/loadui")]
        [InlineData("/framerate")]
        [InlineData("/loc")]
        [InlineData("*dance*")]
        public void WhatOnlyTheClientDoesIsSaidToBeOutOfReach(string line)
        {
            Assert.Equal(ClientCommandKind.ClientOnly, Parse(line).Kind);
        }

        /// <summary>
        /// A room's line (TurbineChat 0xF7DE, a request), in ACE's TurbineChatHandler's read
        /// order: the header of sizes and constants, a cookie, two words, the room by the number
        /// the server gave it, the text in UTF-16 with a short length, the extra-data size, the
        /// speaker, a result and the room's chat type.
        /// </summary>
        [Fact]
        public void ARoomLineIsATurbineChatRequestToTheRoomsNumber()
        {
            ClientCommandContext context = new ClientCommandContext
            {
                PlayerId = Me,
                Cookie = 7,
                Rooms = new Dictionary<TurbineChannel, uint> { [TurbineChannel.General] = 2, [TurbineChannel.Allegiance] = 0x80001424 },
            };

            ClientCommand general = Parse("/cg hello", context);
            Assert.Equal(ClientCommandKind.Message, general.Kind);
            Assert.Equal(Opcodes.TurbineChat, general.Type);

            string inner = "07000000" + "02000000" + "02000000" + "02000000"
                + "05" + Convert.ToHexString(Encoding.Unicode.GetBytes("hello"))
                + "0C000000" + "06000050" + "00000000" + "02000000";
            int innerLength = inner.Length / 2;
            string header = Convert.ToHexString(BitConverter.GetBytes((uint)(innerLength + 32)))
                + "03000000" + "02000000" + "01000000" + "B5000B00" + "01000000" + "B5000B00" + "00000000"
                + Convert.ToHexString(BitConverter.GetBytes((uint)innerLength));
            Assert.Equal(header + inner, Hex(general.Fields));

            ClientCommand allegiance = Parse("/a hi", context);
            Assert.Equal("24140080", Hex(allegiance.Fields).Substring(12 * 8, 8));

            // A room the server has not named, and no allegiance: nothing to send.
            Assert.Equal(ClientCommandKind.NotSent, Parse("/trade wts", context).Kind);
            Assert.Equal(ClientCommandKind.NotSent, Parse("/a hi").Kind);
        }

        // ------------------------------------------------------------------- the host

        private sealed class SendingTransport : IGameTransport, ITypedCommandSource
        {
            public List<AcMessage> Sent { get; } = new List<AcMessage>();

            public string Description => "sending";

#pragma warning disable CS0067 // never raised here
            public event EventHandler<GameMessageEventArgs> MessageReceived;

            public event EventHandler Ended;
#pragma warning restore CS0067

            public bool CanSend => true;

            public bool CanShowInGame => true;

            public List<AcMessage> Shown { get; } = new List<AcMessage>();

            public Task ShowInGameAsync(AcMessage message, CancellationToken cancellationToken = default)
            {
                Shown.Add(message);
                return Task.CompletedTask;
            }

            public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task SendAsync(AcMessage message, CancellationToken cancellationToken = default)
            {
                Sent.Add(message);
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;

            public Func<string, bool> IsPluginCommand { get; set; }

            public event EventHandler<string> CommandTyped;

            public void Type(string line) => CommandTyped?.Invoke(this, line);
        }

        private sealed class CommandPlugin : IPlugin, IChatCommands
        {
            private readonly string _word;

            public CommandPlugin(string name, string word)
            {
                Name = name;
                _word = word;
            }

            public string Name { get; }

            public List<string> Taken { get; } = new List<string>();

            public IReadOnlyCollection<string> CommandWords => _word == null ? Array.Empty<string>() : new[] { _word };

            public void Startup(IHost host)
            {
            }

            public void Shutdown()
            {
            }

            public bool TryCommand(string text)
            {
                if (_word == null || !text.TrimStart('/', '@').StartsWith(_word + " ", StringComparison.Ordinal))
                    return false;
                Taken.Add(text);
                return true;
            }
        }

        private static (GameHost Host, SendingTransport Transport) Host(IReadOnlyDictionary<string, string> settings = null)
        {
            SendingTransport transport = new SendingTransport();
            GameHost host = new GameHost(transport, new ListLog(), settings);
            host.WorldState.SetPlayerId(Me);
            return (host, transport);
        }

        [Fact]
        public void AnotherPluginsCommandGoesToItAndNotToTheServer()
        {
            (GameHost host, SendingTransport transport) = Host();
            CommandPlugin magTools = new CommandPlugin("Mag-Tools", "mt");
            CommandPlugin tank = new CommandPlugin("VirindiTank", "vt");
            host.AddPlugin(magTools);
            host.AddPlugin(tank);

            Assert.Equal(ChatCommandOutcome.Plugin, host.RunChatCommand("/mt use Dwennon", tank));
            Assert.Equal(new[] { "/mt use Dwennon" }, magTools.Taken);
            Assert.Empty(transport.Sent);

            // The plugin asking is not offered its own line back.
            Assert.Equal(ChatCommandOutcome.Sent, host.RunChatCommand("/vt oops", tank));
            Assert.Empty(tank.Taken);
        }

        [Fact]
        public void TheClientsCommandIsSentAsTheClientWouldSendIt()
        {
            (GameHost host, SendingTransport transport) = Host();

            Assert.Equal(ChatCommandOutcome.Sent, host.RunChatCommand("/f !start", null));
            Assert.Equal(ChatCommandOutcome.Sent, host.RunChatCommand("/ls", null));

            Assert.Equal(2, transport.Sent.Count);
            Assert.Equal(Opcodes.GameAction, transport.Sent[0].Opcode);
            Assert.Equal("47010000" + "00080000" + Text("!start"), Convert.ToHexString(transport.Sent[0].Payload.Span.Slice(4)));
            Assert.Equal("63000000", Convert.ToHexString(transport.Sent[1].Payload.Span.Slice(4)));
        }

        [Fact]
        public void WhatCannotBeSentIsSaidAndNotSent()
        {
            (GameHost host, SendingTransport transport) = Host();

            Assert.Equal(ChatCommandOutcome.ClientOnly, host.RunChatCommand("/loadui", null));
            Assert.Equal(ChatCommandOutcome.NotSent, host.RunChatCommand("/r hello", null));
            Assert.Equal(ChatCommandOutcome.Empty, host.RunChatCommand("  ", null));

            host.ActionsAllowed = false;
            Assert.Equal(ChatCommandOutcome.NotSent, host.RunChatCommand("/ls", null));
            Assert.Empty(transport.Sent);
        }

        /// <summary>"/r" answers whoever sent the last tell; "/rt" tells the last one told again.</summary>
        [Fact]
        public void ReplyingAnswersTheLastTell()
        {
            (GameHost host, SendingTransport transport) = Host();
            host.WorldState.NotifyChat(new ChatMessage(ChatKind.Tell, "you there?", "Alice", 0x50000010, 3));

            host.RunChatCommand("/r yes", null);
            host.RunChatCommand("/t Bob, hi", null);
            host.RunChatCommand("/rt bye", null);

            Assert.Equal("5D000000" + Text("yes") + Text("Alice"), Convert.ToHexString(transport.Sent[0].Payload.Span.Slice(4)));
            Assert.Equal("5D000000" + Text("bye") + Text("Bob"), Convert.ToHexString(transport.Sent[2].Payload.Span.Slice(4)));
        }

        // ------------------------------------------------------------------- lines the player types

        /// <summary>
        /// The relay asks, on its own thread, whether a typed line is a plugin's: "/" or "@", then
        /// one of the plugins' words, then a space or nothing. Everything else goes to the server.
        /// </summary>
        [Fact]
        public async Task ATypedLineIsAPluginsWhenItsFirstWordIs()
        {
            (GameHost host, SendingTransport transport) = Host(new Dictionary<string, string> { ["Decal:CommandWords"] = "mt, /it, /" });
            host.AddPlugin(new CommandPlugin("VirindiTank", "vt"));
            await host.StartAsync();
            try
            {
                Func<string, bool> isPlugins = transport.IsPluginCommand;
                Assert.NotNull(isPlugins);

                // Plugins start on the game thread; the words are there once they have.
                SpinWait.SpinUntil(() => isPlugins("/vt start"), TimeSpan.FromSeconds(5));

                Assert.True(isPlugins("/vt start"));
                Assert.True(isPlugins("@vt opt set EnableNav true"));
                Assert.True(isPlugins("/VT start"));
                Assert.True(isPlugins("/vt"));
                Assert.True(isPlugins("/mt use Dwennon"));
                Assert.True(isPlugins("@it recomp"));
                Assert.False(isPlugins("/vtx start"));
                Assert.False(isPlugins("/f hello"));
                Assert.False(isPlugins("vt start"));
                Assert.False(isPlugins("@acehelp"));

                // A listed word of nothing but a slash claims nothing.
                Assert.False(isPlugins("/ hello"));
                Assert.False(isPlugins("/"));
            }
            finally
            {
                await host.DisposeAsync();
            }
        }

        [Fact]
        public async Task ATypedLineGoesToThePluginOnTheGameThread()
        {
            (GameHost host, SendingTransport transport) = Host();
            CommandPlugin tank = new CommandPlugin("VirindiTank", "vt");
            host.AddPlugin(tank);
            await host.StartAsync();
            try
            {
                transport.Type("@vt start");
                transport.Type("/vt");
                SpinWait.SpinUntil(() => host.Statistics.TypedCommands == 2, TimeSpan.FromSeconds(5));

                Assert.Equal(new[] { "@vt start" }, tank.Taken);

                // Kept from the server and taken by nobody: said so, not lost in silence.
                Assert.Single(transport.Shown);
            }
            finally
            {
                await host.DisposeAsync();
            }
        }

        /// <summary>The relay reads a typed line out of the Talk the client sends it in.</summary>
        [Fact]
        public void ATypedLineIsReadOutOfTheClientsTalk()
        {
            byte[] talk = Convert.FromHexString("2A000000" + "15000000" + Text("/vt start"));

            Assert.True(TypedLine.TryRead(Opcodes.GameAction, talk, out string text));
            Assert.Equal("/vt start", text);

            Assert.False(TypedLine.TryRead(Opcodes.GameAction, Convert.FromHexString("2A000000" + "5D000000" + Text("x")), out _));
            Assert.False(TypedLine.TryRead(Opcodes.GameEvent, talk, out _));
            Assert.False(TypedLine.TryRead(Opcodes.GameAction, Convert.FromHexString("2A000000"), out _));
        }

        // ------------------------------------------------------------------- chat emotes

        /// <summary>
        /// A ChatPoseTable as the client's archive lays it out (0x0E000007): its id, the poses and
        /// the command each names, then the commands' words - a few of the client's own rows.
        /// </summary>
        private static byte[] PoseTable()
        {
            (string Pose, string Command)[] poses =
            {
                ("dance", "DrudgeDanceState"), ("wave", "Wave"), ("AFK", "AFKState"), ("smack head", "SmackHead"), ("come here", "Beckon"),
            };
            (string Command, string Mine, string Others)[] emotes =
            {
                ("DrudgeDanceState", "dance, \"Look at me! I'm dancin crazy!\"", "dances, \"Look at me! I'm dancin crazy!\""),
                ("Wave", "wave.", "waves."),
                ("AFKState", "decide to rest for a while.", "decides to rest for a while."),
                ("SmackHead", "smack your head.", "smacks %p head."),
                ("Beckon", "beckon, \"Come here!\"", "beckons, \"Come here!\""),
            };

            WireWriter table = new WireWriter(AC.Dat.ChatPoseTable.FileId).U16((ushort)poses.Length).U16(32);
            foreach ((string pose, string command) in poses)
                table.String16L(pose).String16L(command);
            table.U16((ushort)emotes.Length).U16(16);
            foreach ((string command, string mine, string others) in emotes)
                table.String16L(command).String16L(mine).String16L(others);
            return table.ToArray();
        }

        private static readonly AC.Dat.ChatPoseTable Poses = AC.Dat.ChatPoseTable.Parse(PoseTable());

        [Fact]
        public void TheClientsPoseTableNamesEachPosesEmoteAndItsWords()
        {
            Assert.Equal(5, Poses.Poses.Count);
            AC.Dat.ChatEmote dance = Poses.Find("dance");
            Assert.Equal("DrudgeDanceState", dance.Command);
            Assert.Equal("dances, \"Look at me! I'm dancin crazy!\"", dance.Others);
            Assert.Equal("dance, \"Look at me! I'm dancin crazy!\"", dance.Mine);

            Assert.Equal("AFKState", Poses.Find("afk").Command);
            Assert.Equal("Beckon", Poses.Find("Come Here").Command);
            Assert.Null(Poses.Find("moonwalk"));
            Assert.Null(AC.Dat.ChatPoseTable.Parse(PoseTable().AsSpan(0, 40)));
        }

        /// <summary>
        /// "*dance*" is the emote's motion and its words for everyone else, the SoulEmote
        /// (0x01E1); a lasting motion, a one-off one, "%p" for the character's gender, a pose with
        /// spaces, and a word the table does not have said aloud.
        /// </summary>
        [Fact]
        public void AChatEmoteIsItsMotionAndItsWordsForEveryoneElse()
        {
            ClientCommandContext context = new ClientCommandContext { ChatEmotes = Poses.Find };

            ClientCommand dance = Parse("*dance*", context);
            Assert.Equal(ClientCommandKind.Emote, dance.Kind);
            Assert.Equal(0x43000144u, dance.Motion);
            Assert.Equal(GameActions.SoulEmote, dance.Type);
            Assert.Equal(Text("dances, \"Look at me! I'm dancin crazy!\""), Hex(dance.Fields));

            Assert.Equal(0x13000087u, Parse("*wave*", context).Motion);
            Assert.Equal(0x1300007Au, Parse("*come here*", context).Motion);
            Assert.Equal("smacks his head.", Parse("*smack head*", context).EmoteText);
            Assert.Equal("smacks her head.", Parse("*smack head*", new ClientCommandContext { ChatEmotes = Poses.Find, Gender = 2 }).EmoteText);

            ClientCommand unknown = Parse("*moonwalk*", context);
            Assert.Equal(GameActions.Talk, unknown.Type);
            Assert.Equal(Text("*moonwalk*"), Hex(unknown.Fields));
        }

        [Fact]
        public void EveryEmoteTheClientHasWordsForHasItsMotion()
        {
            string[] commands =
            {
                "Helper", "Cheer", "Shiver", "HaveASeat", "NudgeLeft", "ATOYOT", "Teapot", "Spit", "SmackHead", "ShakeFist",
                "AtEaseState", "PossumState", "PointLeftState", "SitCrossleggedState", "SitState", "WAVESTATE", "CLAPHANDSSTATE",
                "PrayState", "WindedState", "SurrenderState", "TapFootState", "SaluteState", "Wave", "PointState", "YawnStretch",
                "WaveHigh", "HeartyLaugh", "MimeDrink", "YMCA", "WARMHANDS", "ClapHands", "BlowKiss", "PointRight", "PointLeft",
                "NudgeRight", "MimeEat", "ScratchHead", "ShakeHead", "Nod", "DrudgeDance", "HaveASeatState", "ThinkerState",
                "ReadState", "DrudgeDanceState", "PointDownState", "TalktotheHandState", "PointRightState", "SitBackState",
                "MeditateState", "AFKState", "CurtseyState", "SNOWANGELSTATE", "SCRATCHHEADSTATE", "SHAKEFISTSTATE",
                "CrossArmsState", "LeanState", "WoahState", "SlouchState", "PleadState", "KneelState", "Cringe", "AkimboState",
                "BowDeepState", "BeSeeingYou", "WaveLow", "Shrug", "Laugh", "Cry", "Knock", "Mock", "ScanHorizon", "PointDown",
                "Beckon", "Shoo",
            };

            // The 74 the client's ChatEmoteHash has, each a chat emote (0x02000000) of one class or the other.
            Assert.Equal(74, commands.Length);
            foreach (string command in commands)
            {
                uint motion = ChatEmoteCommands.Find(command);
                Assert.True((motion & 0x02000000) != 0, command);
                Assert.True(((motion & 0x10000000) != 0) ^ ((motion & 0x40000000) != 0), command);
            }

            Assert.Equal(0u, ChatEmoteCommands.Find("Moonwalk"));
        }

        /// <summary>The client's own tables, as far as the host answers for them: only the chat poses.</summary>
        private sealed class PoseData : IGameData
        {
            public bool IsAvailable => true;

            public string GetSpellName(uint spellId) => null;

            public System.Drawing.Color? GetSlotColor(uint paletteId, int offset, int length) => null;

            public bool TryGetSkillFormula(uint skillId, out uint attribute1, out uint attribute2, out uint divisor)
            {
                attribute1 = attribute2 = divisor = 0;
                return false;
            }

            public bool TryGetVitalFormula(uint vitalId, out uint attribute1, out uint attribute2, out uint divisor)
                => TryGetSkillFormula(vitalId, out attribute1, out attribute2, out divisor);

            public AC.Dat.SpellInfo GetSpell(uint spellId) => null;

            public IReadOnlyCollection<AC.Dat.SpellInfo> Spells => Array.Empty<AC.Dat.SpellInfo>();

            public IReadOnlyList<AC.Dat.SpellInfo> GetSpellsInCategory(uint category) => Array.Empty<AC.Dat.SpellInfo>();

            public AC.Dat.SpellInfo FindSpell(string name) => null;

            public AC.Dat.SpellComponentInfo GetComponent(uint componentId) => null;

            public AC.Dat.ChatEmote GetChatEmote(string pose) => Poses.Find(pose);
        }

        /// <summary>A host whose character stands where the client last said, in <paramref name="motion"/>.</summary>
        private static (GameHost Host, SendingTransport Transport) EmoteHost(ClientMotionState motion)
        {
            (GameHost host, SendingTransport transport) = Host();
            host.WorldState.GameData = new PoseData();
            host.WorldState.SetClientMotion(motion, new Location(0xA9B4001F, 10f, 20f, 30f, 1f, 0f, 0f, 0f),
                new MovementSequences(4, 5, 6, 7, 1));
            return (host, transport);
        }

        private static ClientMotionState Standing(uint style = ChatEmoteCommands.NonCombat, uint forward = 0)
            => new ClientMotionState
            {
                Flags = MotionFlags.CurrentHoldKey | MotionFlags.CurrentStyle | (forward != 0 ? MotionFlags.ForwardCommand : 0),
                CurrentHoldKey = 2,
                CurrentStyle = style,
                ForwardCommand = forward,
            };

        /// <summary>What follows a MoveToState's motion: where the client stood, its sequences and its contact.</summary>
        private const string Where = "1F00B4A9" + "00002041" + "0000A041" + "0000F041" + "0000803F" + "00000000" + "00000000" + "00000000"
            + "0400" + "0500" + "0600" + "0700" + "01000000";

        /// <summary>
        /// "*wave*" goes as the client sent it: a MoveToState standing where the client stood, the
        /// wave as one action after the fields - its low half, a stamp marked the client's, speed 1 -
        /// then the SoulEmote with the words.
        /// </summary>
        [Fact]
        public void AWaveIsAnActionInAMoveToStateThenTheWords()
        {
            (GameHost host, SendingTransport transport) = EmoteHost(Standing());

            Assert.Equal(ChatCommandOutcome.Sent, host.RunChatCommand("*wave*", null));

            Assert.Equal(2, transport.Sent.Count);
            Assert.Equal("1CF60000" + "03080000" + "02000000" + "3D000080" + "8700" + "0180" + "0000803F" + Where,
                Convert.ToHexString(transport.Sent[0].Payload.Span.Slice(4)));
            Assert.Equal("E1010000" + Text("waves."), Convert.ToHexString(transport.Sent[1].Payload.Span.Slice(4)));
        }

        /// <summary>"*dance*" is a state the character stays in: the forward command, with no actions.</summary>
        [Fact]
        public void ADanceIsTheForwardCommand()
        {
            (GameHost host, SendingTransport transport) = EmoteHost(Standing());

            host.RunChatCommand("*dance*", null);

            Assert.Equal("1CF60000" + "07000000" + "02000000" + "3D000080" + "44010043" + Where,
                Convert.ToHexString(transport.Sent[0].Payload.Span.Slice(4)));
            Assert.Equal(GameActions.SoulEmote, BitConverter.ToUInt32(transport.Sent[1].Payload.Span.Slice(4)));
        }

        /// <summary>
        /// In a combat stance or walking the client refused the motion in its own words, and sent
        /// the words of the emote all the same.
        /// </summary>
        [Theory]
        [InlineData(0x8000003Cu, 0u, "You can't use chat emotes in combat mode")]
        [InlineData(ChatEmoteCommands.NonCombat, 0x45000005u, "You can't use chat emotes from this position")]
        public void AnEmoteTheClientWouldNotPlayIsOnlyItsWords(uint style, uint forward, string refusal)
        {
            (GameHost host, SendingTransport transport) = EmoteHost(Standing(style, forward));

            Assert.Equal(ChatCommandOutcome.Sent, host.RunChatCommand("*wave*", null));

            Assert.Equal(GameActions.SoulEmote, BitConverter.ToUInt32(transport.Sent.Single().Payload.Span.Slice(4)));
            Assert.Contains(refusal, Encoding.Latin1.GetString(transport.Shown.Single().Payload.ToArray()));
        }

        /// <summary>Without the client's archive no emote can be told from any other line, and none is sent.</summary>
        [Fact]
        public void WithoutTheClientsTablesAnEmoteIsOutOfReach()
        {
            (GameHost host, SendingTransport transport) = Host();

            Assert.Equal(ChatCommandOutcome.ClientOnly, host.RunChatCommand("*dance*", null));
            Assert.Empty(transport.Sent);
        }

        /// <summary>
        /// The client's own MoveToState with an emote in it is read whole: the action, then the
        /// position after it, where it would otherwise have been read from the action's bytes.
        /// </summary>
        [Fact]
        public void TheClientsOwnEmoteIsReadWithItsPosition()
        {
            WorldState world = new WorldState(() => DateTimeOffset.UnixEpoch);
            byte[] move = Convert.FromHexString("05000000" + "1CF60000" + "03080000" + "01000000" + "3D000080" + "8700" + "0280" + "0000803F" + Where);

            MessageDecoder.Apply(AcMessage.Create(Opcodes.GameAction, move), PacketDirection.Outbound, world);

            ClientMotionState motion = world.Character.Motion;
            Assert.Equal(MotionFlags.CurrentHoldKey | MotionFlags.CurrentStyle, motion.Flags);
            Assert.Equal((ushort)0x0087, motion.Actions.Single().Command);
            Assert.Equal((ushort)0x8002, motion.Actions.Single().Stamp);
            Assert.Equal(0xA9B4001Fu, world.Character.Location.Value.LandblockCell);
            Assert.Equal(20f, world.Character.Location.Value.Y);
            Assert.Equal((ushort)7, world.Character.Sequences.ForcePosition);
        }
    }
}
