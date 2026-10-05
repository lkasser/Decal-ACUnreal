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
