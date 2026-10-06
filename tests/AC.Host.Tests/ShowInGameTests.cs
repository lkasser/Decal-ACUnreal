using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AC.Host.Decoding;
using AC.Host.Plugins;
using AC.Host.Transport;
using AC.Host.World;
using AC.Protocol;
using AC.Proxy;
using Xunit;

namespace AC.Host.Tests
{
    /// <summary>
    /// Putting a line in the game's own chat window. The client cannot be drawn into, so
    /// this is the only way anything appears inside the game - which makes it worth
    /// checking that what goes out is a message the client can actually read, rather than
    /// bytes that merely leave.
    /// </summary>
    public class ShowInGameTests
    {
        private sealed class ShowingTransport : IGameTransport
        {
            public ShowingTransport(bool canShow = true) => CanShowInGame = canShow;

            public List<AcMessage> Shown { get; } = new List<AcMessage>();

            public string Description => "showing";

            public event EventHandler<GameMessageEventArgs> MessageReceived;

            public event EventHandler Ended;

            public bool CanSend => true;

            public bool CanShowInGame { get; }

            public Task StartAsync(CancellationToken cancellationToken = default)
            {
                // Ends at once, so a host over this transport finishes rather than hangs.
                Ended?.Invoke(this, EventArgs.Empty);
                return Task.CompletedTask;
            }

            public Task SendAsync(AcMessage message, CancellationToken cancellationToken = default)
                => Task.CompletedTask;

            public Task ShowInGameAsync(AcMessage message, CancellationToken cancellationToken = default)
            {
                Shown.Add(message);
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync()
            {
                MessageReceived = null;
                return ValueTask.CompletedTask;
            }
        }

        private static GameHost Host(ShowingTransport transport, out ListLog log)
        {
            log = new ListLog();
            return new GameHost(transport, log, dataRoot: Path.Combine(Path.GetTempPath(), "achost-tests"));
        }

        [Fact]
        public async Task ALineShownInGameIsAMessageTheClientCanRead()
        {
            ShowingTransport transport = new ShowingTransport();
            await using GameHost host = Host(transport, out _);

            Assert.True(host.ShowInGame("Keeping Frost Dolabra"));

            AcMessage message = Assert.Single(transport.Shown);
            Assert.Equal(Opcodes.ServerMessage, message.Opcode);

            // Read it back the way the client would: through the decoder that handles
            // every other ServerMessage from a real session.
            WorldState world = new WorldState();
            List<ChatMessage> chat = new List<ChatMessage>();
            world.ChatReceived += (_, m) => chat.Add(m);

            Assert.Equal(DecodeOutcome.Applied, MessageDecoder.Apply(message, PacketDirection.Inbound, world));

            ChatMessage line = Assert.Single(chat);
            Assert.Equal("[Decal] Keeping Frost Dolabra", line.Text);
        }

        /// <summary>
        /// The line arrives as though the server sent it, which it did not. A prefix is
        /// the difference between reporting and impersonating, so it is not optional - and
        /// it is the host's, which stands where Decal did, not any one plugin's.
        /// </summary>
        [Fact]
        public async Task ALineSaysItIsOurs()
        {
            ShowingTransport transport = new ShowingTransport();
            await using GameHost host = Host(transport, out _);

            host.ShowInGame("anything at all");

            ChatMessage line = ReadBack(transport.Shown[0]);
            Assert.StartsWith("[Decal] ", line.Text);
            Assert.Equal(0x0Du, line.ChatType);
        }

        /// <summary>
        /// A plugin's own line - Virindi Tank's "[VTank] ...", a Decal plugin's "[VGI] ..." -
        /// arrives exactly as written, in the chat type it named, which the client draws it in
        /// the colour of: nothing of the host's is added.
        /// </summary>
        [Theory]
        [InlineData("[VTank] Force buff enabled.", 7)]
        [InlineData("[VGI] This character is not selected for tracking.", 5)]
        [InlineData("[VI] Disconnected, retry in 15 seconds.", 0)]
        [InlineData("[VTank] Error: no usable spell detected in the same class as \"Incantation of Frost Bolt\"", 6)]
        public async Task APluginsLineArrivesAsWrittenInItsChatType(string text, int chatType)
        {
            ShowingTransport transport = new ShowingTransport();
            await using GameHost host = Host(transport, out _);

            Assert.True(host.ShowInGame(text, chatType));

            AcMessage message = Assert.Single(transport.Shown);
            Assert.Equal(Opcodes.ServerMessage, message.Opcode);
            ChatMessage line = ReadBack(message);
            Assert.Equal(ChatKind.System, line.Kind);
            Assert.Equal(text, line.Text);
            Assert.Equal((uint)chatType, line.ChatType);
        }

        /// <summary>
        /// A link the old client drew - Mag-Tools' item line, with its closing tag's two
        /// backslashes; the server's own around a speaker's name - arrives as the text it was
        /// around, as the old client showed it: AC:Unreal's chat has no links and would show the
        /// markup. Mag-Tools' "&lt;{Mag-Tools}&gt;" is its name, not markup, and stays.
        /// </summary>
        [Theory]
        [InlineData(@"<Tell:IIDString:221112:-2147399821>-<\\Tell> Black Opal Heavy Bracelet, Legendary Frost Ward, Wield Lvl 150",
                    "- Black Opal Heavy Bracelet, Legendary Frost Ward, Wield Lvl 150")]
        [InlineData(@"<Tell:IIDString:221112:-2024140046>+(Epics)<\\Tell> Ivory Ring", "+(Epics) Ivory Ring")]
        [InlineData(@"<Tell:IIDString:1343111160:Character Y>Character Y<\Tell> says, ""hello""", @"Character Y says, ""hello""")]
        [InlineData(@"[Allegiance] <Tell:IIDString:0:Thrungus>Thrungus<\Tell> says, ""kk""", @"[Allegiance] Thrungus says, ""kk""")]
        [InlineData("<{Mag-Tools}>: Plugin now online.", "<{Mag-Tools}>: Plugin now online.")]
        [InlineData("a < b > c, and Tell me", "a < b > c, and Tell me")]
        public async Task AChatLinkArrivesAsItsText(string text, string shown)
        {
            ShowingTransport transport = new ShowingTransport();
            await using GameHost host = Host(transport, out _);

            Assert.True(host.ShowInGame(text, 14));

            Assert.Equal(shown, ReadBack(Assert.Single(transport.Shown)).Text);
            Assert.Equal(shown, ChatMarkup.Visible(text));
        }

        [Fact]
        public async Task APluginsLineIsRefusedLikeTheHostsWhenNothingCanBeShown()
        {
            ShowingTransport transport = new ShowingTransport();
            await using GameHost host = Host(transport, out _);
            Assert.False(host.ShowInGame(null, 7));
            Assert.False(host.ShowInGame(string.Empty, 7));
            Assert.Empty(transport.Shown);

            ShowingTransport closed = new ShowingTransport(canShow: false);
            await using GameHost unreachable = Host(closed, out _);
            Assert.False(unreachable.ShowInGame("[VTank] nobody will see this", 7));
            Assert.Empty(closed.Shown);
        }

        /// <summary>A shown line read back the way the client would, through the decoder every real ServerMessage goes through.</summary>
        private static ChatMessage ReadBack(AcMessage message)
        {
            WorldState world = new WorldState();
            ChatMessage line = null;
            world.ChatReceived += (_, m) => line = m;
            Assert.Equal(DecodeOutcome.Applied, MessageDecoder.Apply(message, PacketDirection.Inbound, world));
            return line;
        }

        [Fact]
        public async Task ATransportThatCannotReachTheClientRefusesRatherThanThrowing()
        {
            ShowingTransport transport = new ShowingTransport(canShow: false);
            await using GameHost host = Host(transport, out ListLog log);

            Assert.False(host.ShowInGame("nobody will see this"));

            Assert.Empty(transport.Shown);
            Assert.DoesNotContain(log.Lines, l => l.StartsWith("ERROR"));
        }

        [Fact]
        public async Task NothingIsSentForAnEmptyLine()
        {
            ShowingTransport transport = new ShowingTransport();
            await using GameHost host = Host(transport, out _);

            Assert.False(host.ShowInGame(null));
            Assert.False(host.ShowInGame(string.Empty));
            Assert.Empty(transport.Shown);
        }

        [Fact]
        public async Task LinesAreCounted()
        {
            ShowingTransport transport = new ShowingTransport();
            await using GameHost host = Host(transport, out _);

            host.ShowInGame("one");
            host.ShowInGame("two");

            Assert.Equal(2, host.Statistics.ShownInGame);
        }

        /// <summary>
        /// A line too long for one fragment - which is all the relay slips into the server's
        /// stream - goes as several that each fit, broken at spaces, with nothing lost: Virindi
        /// Tank's 1,470-character list of meta functions, sent whole, never showed at all.
        /// </summary>
        [Fact]
        public async Task ALongLineGoesAsSeveralThatEachFitOneFragment()
        {
            ShowingTransport transport = new ShowingTransport();
            await using GameHost host = Host(transport, out _);
            string text = "[VTank] " + string.Join(", ", Enumerable.Range(0, 140).Select(i => "getcharintprop" + i + "[1]"));
            Assert.True(text.Length > 1400);

            Assert.True(host.ShowInGame(text, 7));

            Assert.True(transport.Shown.Count > 2);
            List<string> lines = transport.Shown.Select(m => ReadBack(m).Text).ToList();
            foreach (AcMessage message in transport.Shown)
                Assert.Single(PacketWriter.Fragment(message.Opcode, message.Payload.Span, 1));
            Assert.All(lines, l => Assert.True(l.Length <= GameHost.LongestLineShown));
            Assert.Equal(text, string.Join(" ", lines));
            Assert.All(transport.Shown, m => Assert.Equal(7u, ReadBack(m).ChatType));
            Assert.Equal(transport.Shown.Count, host.Statistics.ShownInGame);
        }

        /// <summary>The longest line that fits is sent as it is; one with no space to break at is cut where it must be.</summary>
        [Fact]
        public async Task ALineIsBrokenOnlyWhenItMustBeAndCutWhereThereIsNoSpace()
        {
            ShowingTransport transport = new ShowingTransport();
            await using GameHost host = Host(transport, out _);
            string longest = new string('a', GameHost.LongestLineShown);
            string unbroken = new string('b', GameHost.LongestLineShown + 10);

            host.ShowInGame(longest, 0);
            host.ShowInGame(unbroken, 0);

            Assert.Equal(3, transport.Shown.Count);
            Assert.Single(PacketWriter.Fragment(transport.Shown[0].Opcode, transport.Shown[0].Payload.Span, 1));
            Assert.Equal(longest, ReadBack(transport.Shown[0]).Text);
            Assert.Equal(new string('b', GameHost.LongestLineShown), ReadBack(transport.Shown[1]).Text);
            Assert.Equal(new string('b', 10), ReadBack(transport.Shown[2]).Text);

            // One character more than the longest would need a second fragment.
            AcMessage tooLong = new WireWriter(Opcodes.ServerMessage).String16L(new string('c', GameHost.LongestLineShown + 1)).U32(0).ToMessage();
            Assert.Equal(2, PacketWriter.Fragment(tooLong.Opcode, tooLong.Payload.Span, 1).Count);
        }

        /// <summary>
        /// Text of awkward lengths, because the payload pads to a four-byte boundary and
        /// an off-by-one there produces a message that leaves cleanly and reads as rubbish.
        /// </summary>
        [Theory]
        [InlineData("a")]
        [InlineData("ab")]
        [InlineData("abc")]
        [InlineData("abcd")]
        [InlineData("abcde")]
        [InlineData("Took Covenant Shield [Keep good armour]")]
        public async Task TextOfAnyLengthSurvivesTheRoundTrip(string text)
        {
            ShowingTransport transport = new ShowingTransport();
            await using GameHost host = Host(transport, out _);

            Assert.True(host.ShowInGame(text));
            Assert.True(host.ShowInGame(text, 14));

            WorldState world = new WorldState();
            string read = null;
            world.ChatReceived += (_, m) => read = m.Text;

            Assert.Equal(DecodeOutcome.Applied, MessageDecoder.Apply(transport.Shown[0], PacketDirection.Inbound, world));
            Assert.Equal("[Decal] " + text, read);

            // Unprefixed, the text alone decides the padding.
            ChatMessage plain = ReadBack(transport.Shown[1]);
            Assert.Equal(text, plain.Text);
            Assert.Equal(14u, plain.ChatType);
        }
    }
}
