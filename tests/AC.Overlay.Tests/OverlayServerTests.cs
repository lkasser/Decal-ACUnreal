using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AC.Host.Overlay;
using Xunit;

namespace AC.Overlay.Tests
{
    /// <summary>
    /// Stands in for the injected overlay, and reads and writes frames by hand rather than
    /// through the server's own helpers - the point of these tests is that the wire format
    /// is what it is documented to be, which a test sharing the server's code could not
    /// show.
    /// </summary>
    internal sealed class FakeOverlay : IAsyncDisposable
    {
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

        private readonly NamedPipeClientStream _pipe;

        private FakeOverlay(NamedPipeClientStream pipe)
        {
            _pipe = pipe;
        }

        public static async Task<FakeOverlay> ConnectAsync(OverlayServer server)
        {
            NamedPipeClientStream pipe = new NamedPipeClientStream(
                ".",
                server.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            await pipe.ConnectAsync((int)Patience.TotalMilliseconds);
            return new FakeOverlay(pipe);
        }

        /// <summary>One frame: four bytes of little-endian byte count, then that many bytes.</summary>
        public async Task<byte[]> ReadFrameAsync()
        {
            using CancellationTokenSource timeout = new CancellationTokenSource(Patience);

            byte[] prefix = new byte[4];
            await ReadExactlyAsync(prefix, timeout.Token);

            byte[] body = new byte[BinaryPrimitives.ReadInt32LittleEndian(prefix)];
            await ReadExactlyAsync(body, timeout.Token);
            return body;
        }

        public async Task<OverlayState> ReadStateAsync()
            => OverlayJson.ReadState(Encoding.UTF8.GetString(await ReadFrameAsync()));

        public async Task SendAsync(OverlayCommand command)
        {
            byte[] body = Encoding.UTF8.GetBytes(OverlayJson.ToJson(command));
            byte[] frame = new byte[4 + body.Length];
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), body.Length);
            body.CopyTo(frame, 4);

            using CancellationTokenSource timeout = new CancellationTokenSource(Patience);
            await _pipe.WriteAsync(frame, timeout.Token);
            await _pipe.FlushAsync(timeout.Token);
        }

        /// <summary>Sends a frame that promises more bytes than it carries.</summary>
        public async Task SendNonsenseAsync(int claimedLength)
        {
            byte[] prefix = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(prefix, claimedLength);

            using CancellationTokenSource timeout = new CancellationTokenSource(Patience);
            await _pipe.WriteAsync(prefix, timeout.Token);
            await _pipe.FlushAsync(timeout.Token);
        }

        private async Task ReadExactlyAsync(byte[] buffer, CancellationToken cancellationToken)
        {
            int read = 0;
            while (read < buffer.Length)
            {
                int got = await _pipe.ReadAsync(buffer.AsMemory(read), cancellationToken);
                if (got == 0)
                    throw new EndOfStreamException("The server closed the pipe part way through a frame.");

                read += got;
            }
        }

        public ValueTask DisposeAsync() => _pipe.DisposeAsync();
    }

    public class OverlayServerTests
    {
        /// <summary>
        /// A base name of its own per test. The server appends this process's id, so two
        /// servers in one test run would otherwise fight over the same pipe.
        /// </summary>
        private static string UniqueName() => "achost-overlay-test-" + Guid.NewGuid().ToString("N");

        private static OverlayState Snapshot(string item, string decision = "Keep")
            => OverlayStateBuilder.Build(
                true, true, true, "127.0.0.1:9000", "Frostfell", "0xAB94001C 12.3 45.6 0.0",
                41, 7, 0, 312,
                ("Loot", new[] { "Item", "Decision" }, new[] { OverlayStateBuilder.Row(RowTones.Good, item, decision) }));

        [Fact]
        public async Task AnIdenticalSnapshotIsNotPublishedTwice()
        {
            await using OverlayServer server = new OverlayServer(UniqueName());

            Assert.True(server.Publish(Snapshot("Leather Cap")));
            Assert.False(server.Publish(Snapshot("Leather Cap")));

            Assert.Equal(1L, server.Revision);
            Assert.Equal(1L, server.Published);
            Assert.Equal(1L, server.Skipped);
        }

        [Fact]
        public async Task AChangedSnapshotMovesTheRevisionOnByExactlyOne()
        {
            await using OverlayServer server = new OverlayServer(UniqueName());

            server.Publish(Snapshot("Leather Cap"));
            Assert.Equal(1L, server.Revision);

            server.Publish(Snapshot("Short Sword"));
            Assert.Equal(2L, server.Revision);

            // Back to a snapshot that differs only from the one immediately before it.
            server.Publish(Snapshot("Short Sword"));
            Assert.Equal(2L, server.Revision);

            server.Publish(Snapshot("Short Sword", "Leave"));
            Assert.Equal(3L, server.Revision);
        }

        /// <summary>
        /// The overlay is optional, and a host that stalls whenever nobody has injected it
        /// would be worse than no overlay at all.
        /// </summary>
        [Fact]
        public async Task PublishingWithNoOverlayAttachedNeitherThrowsNorBlocks()
        {
            await using OverlayServer server = new OverlayServer(UniqueName());
            server.Start();

            Stopwatch clock = Stopwatch.StartNew();
            for (int i = 0; i < 500; i++)
                server.Publish(Snapshot("item " + i.ToString()));
            clock.Stop();

            Assert.False(server.IsConnected);
            Assert.Equal(500L, server.Revision);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"500 publishes took {clock.Elapsed}.");
        }

        [Fact]
        public async Task PublishingBeforeTheServerIsEvenStartedIsFine()
        {
            await using OverlayServer server = new OverlayServer(UniqueName());

            Assert.True(server.Publish(Snapshot("Leather Cap")));
            Assert.Equal(1L, server.Revision);
        }

        /// <summary>
        /// The overlay is injected into a client that was already running, so it always
        /// arrives after the host has state to show.
        /// </summary>
        [Fact]
        public async Task AnOverlayThatConnectsLateIsSentTheSnapshotItMissed()
        {
            await using OverlayServer server = new OverlayServer(UniqueName());
            server.Publish(Snapshot("Leather Cap"));
            server.Start();

            await using FakeOverlay overlay = await FakeOverlay.ConnectAsync(server);
            OverlayState received = await overlay.ReadStateAsync();

            Assert.Equal(1L, received.Revision);
            Assert.Equal("Frostfell", received.Status.Character);
            Assert.Equal("Leather Cap", received.Panels[0].Rows[0].Cells[0]);
        }

        [Fact]
        public async Task ACommandFromTheOverlayIsRaisedAsAnEvent()
        {
            TaskCompletionSource<OverlayCommand> raised =
                new TaskCompletionSource<OverlayCommand>(TaskCreationOptions.RunContinuationsAsynchronously);

            await using OverlayServer server = new OverlayServer(UniqueName());
            server.CommandReceived += (_, command) => raised.TrySetResult(command);
            server.Publish(Snapshot("Leather Cap"));
            server.Start();

            await using FakeOverlay overlay = await FakeOverlay.ConnectAsync(server);
            await overlay.ReadStateAsync();
            await overlay.SendAsync(new OverlayCommand { Name = "toggle-looting", Value = "off" });

            OverlayCommand got = await raised.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal("toggle-looting", got.Name);
            Assert.Equal("off", got.Value);
        }

        [Fact]
        public async Task AnOverlayThatGoesMidSessionDoesNotThrowIntoThePublisher()
        {
            List<string> problems = new List<string>();

            await using OverlayServer server = new OverlayServer(UniqueName(), (message, _) =>
            {
                lock (problems) problems.Add(message);
            });

            server.Publish(Snapshot("Leather Cap"));
            server.Start();

            FakeOverlay overlay = await FakeOverlay.ConnectAsync(server);
            await overlay.ReadStateAsync();
            await overlay.DisposeAsync();

            // Whether the server has noticed yet is a race, which is the point: publishing
            // has to be safe either way.
            for (int i = 0; i < 50; i++)
                server.Publish(Snapshot("item " + i.ToString()));

            Assert.Equal(51L, server.Revision);
        }

        [Fact]
        public async Task AnotherOverlayCanConnectAfterTheFirstOneHasGone()
        {
            await using OverlayServer server = new OverlayServer(UniqueName());
            server.Publish(Snapshot("Leather Cap"));
            server.Start();

            FakeOverlay first = await FakeOverlay.ConnectAsync(server);
            await first.ReadStateAsync();
            await first.DisposeAsync();

            server.Publish(Snapshot("Short Sword"));

            await using FakeOverlay second = await FakeOverlay.ConnectAsync(server);
            OverlayState received = await second.ReadStateAsync();

            Assert.Equal(2L, received.Revision);
            Assert.Equal("Short Sword", received.Panels[0].Rows[0].Cells[0]);
        }

        /// <summary>
        /// A length prefix counts bytes, not characters, and it is the reason a payload with
        /// a newline in it needs no escaping rule of its own on the way through the pipe.
        /// </summary>
        [Fact]
        public async Task AFrameCarryingANewlineAndANonAsciiNameArrivesIntact()
        {
            const string awkward = "Frozen Rüschenfüller\nsecond line\tand a tab ✦";

            await using OverlayServer server = new OverlayServer(UniqueName());
            server.Publish(Snapshot(awkward));
            server.Start();

            await using FakeOverlay overlay = await FakeOverlay.ConnectAsync(server);
            byte[] frame = await overlay.ReadFrameAsync();
            string json = Encoding.UTF8.GetString(frame);

            // More bytes than characters: the prefix was a byte count, as documented.
            Assert.True(frame.Length > json.Length, $"{frame.Length} bytes for {json.Length} characters.");

            OverlayState received = OverlayJson.ReadState(json);
            Assert.Equal(awkward, received.Panels[0].Rows[0].Cells[0]);
        }

        /// <summary>
        /// The prefix is four bytes of whatever is on the other side of the pipe. A wrong
        /// one costs the connection and nothing else.
        /// </summary>
        [Fact]
        public async Task AnImpossibleLengthPrefixCostsTheConnectionAndNotTheHost()
        {
            List<string> problems = new List<string>();

            await using OverlayServer server = new OverlayServer(UniqueName(), (message, _) =>
            {
                lock (problems) problems.Add(message);
            });

            server.Publish(Snapshot("Leather Cap"));
            server.Start();

            FakeOverlay overlay = await FakeOverlay.ConnectAsync(server);
            await overlay.ReadStateAsync();
            await overlay.SendNonsenseAsync(int.MaxValue);

            // The server drops the connection, which is how the next one gets in.
            await using FakeOverlay second = await FakeOverlay.ConnectAsync(server);
            Assert.NotEmpty(await second.ReadFrameAsync());

            await overlay.DisposeAsync();

            lock (problems)
                Assert.Contains(problems, p => p.Contains("2147483647"));
        }

        /// <summary>
        /// The default name is used exactly as given, because the injected overlay
        /// connects to it and has no way to learn anything about this process. Deriving a
        /// name the other side cannot compute made the ordinary case fail silently - the
        /// two simply never met - in order to prevent a collision that only arises when
        /// someone deliberately runs two hosts at once.
        /// </summary>
        [Fact]
        public async Task TheDefaultPipeNameIsTheOneTheOverlayConnectsTo()
        {
            await using OverlayServer server = new OverlayServer();

            Assert.Equal(OverlayServer.DefaultPipeName, server.PipeName);
            Assert.Equal("achost-overlay", server.PipeName);
        }

        /// <summary>
        /// Two hosts at once is still supported, but it has to be asked for, and then
        /// both sides are told the same name.
        /// </summary>
        [Fact]
        public async Task TwoHostsCanBeGivenDistinctNamesWhenSomebodyWantsThem()
        {
            Assert.Equal("achost-overlay-4242", OverlayServer.NameFor(OverlayServer.DefaultPipeName, 4242));
            Assert.NotEqual(OverlayServer.NameFor("achost-overlay", 1), OverlayServer.NameFor("achost-overlay", 2));

            string mine = OverlayServer.NameFor("achost-overlay-test", Environment.ProcessId);
            await using OverlayServer server = new OverlayServer(mine);
            Assert.Equal(mine, server.PipeName);
        }

        /// <summary>
        /// The publish time must not count as a change, or every snapshot differs from the
        /// last and the revision climbs on every tick - which is exactly what happened when
        /// the timestamp was added: the overlay was told to redraw four times a second
        /// while nothing about the session had changed.
        /// </summary>
        [Fact]
        public async Task TheClockDoesNotCountAsAChange()
        {
            await using OverlayServer server = new OverlayServer(UniqueName());

            OverlayState first = Snapshot("Leather Cap");
            first.PublishedMs = 1_000;
            Assert.True(server.Publish(first));
            Assert.Equal(1L, server.Revision);

            // The same session, published a second later.
            OverlayState again = Snapshot("Leather Cap");
            again.PublishedMs = 2_000;
            Assert.False(server.Publish(again));
            Assert.Equal(1L, server.Revision);

            // The time still reaches the overlay, which needs it to judge staleness.
            Assert.Equal(2_000L, again.PublishedMs);

            // And a real change still counts.
            OverlayState changed = Snapshot("Short Sword");
            changed.PublishedMs = 3_000;
            Assert.True(server.Publish(changed));
            Assert.Equal(2L, server.Revision);
        }

        [Fact]
        public async Task DisposingTwiceIsHarmless()
        {
            OverlayServer server = new OverlayServer(UniqueName());
            server.Start();
            server.Publish(Snapshot("Leather Cap"));

            await server.DisposeAsync();
            await server.DisposeAsync();

            // Still no throw from a host that publishes on its way out.
            Assert.False(server.Publish(Snapshot("Leather Cap")));
        }
    }
}
