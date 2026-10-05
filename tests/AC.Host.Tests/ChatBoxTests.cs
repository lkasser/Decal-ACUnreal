using System;
using System.Collections.Generic;
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
    }
}
