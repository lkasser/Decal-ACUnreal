using System;
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
    /// A quiet host must still be seen to be alive. Taking the clock out of the change
    /// comparison was right - it stopped the revision climbing on every tick - but it
    /// meant an idle host sent nothing at all, and the overlay, which judges liveness by
    /// the timestamp it last received, declared a healthy host stale after a quiet
    /// minute. It did, on screen, in the owner's own screenshot: "not published for 581
    /// seconds" against a host that was fine.
    /// </summary>
    public class OverlayHeartbeatTests
    {
        private static string UniqueName() => "achost-overlay-heartbeat-" + Guid.NewGuid().ToString("N");

        private static OverlayState Snapshot(string item, long publishedMs)
        {
            OverlayState state = OverlayStateBuilder.Build(
                connected: true, acting: false, looting: false,
                server: "Example Server", character: "Testchar I", position: string.Empty,
                messagesIn: 1, messagesOut: 1, malformed: 0, objects: 1,
                ("Loot", new[] { "Item" }, new[] { OverlayStateBuilder.Row(0, item) }));

            state.PublishedMs = publishedMs;
            return state;
        }

        /// <summary>
        /// Reads one length-prefixed frame off the pipe as the injected side would, so the
        /// test sees exactly what the overlay sees rather than what the server believes.
        /// </summary>
        private static async Task<OverlayState> ReadFrameAsync(NamedPipeClientStream pipe)
        {
            byte[] prefix = new byte[4];
            int got = 0;
            while (got < 4)
            {
                int n = await pipe.ReadAsync(prefix, got, 4 - got).WaitAsync(TimeSpan.FromSeconds(5));
                if (n == 0) throw new EndOfStreamException("pipe closed");
                got += n;
            }

            int length = BitConverter.ToInt32(prefix, 0);
            byte[] body = new byte[length];
            got = 0;
            while (got < length)
            {
                int n = await pipe.ReadAsync(body, got, length - got).WaitAsync(TimeSpan.FromSeconds(5));
                if (n == 0) throw new EndOfStreamException("pipe closed");
                got += n;
            }

            return OverlayJson.ReadState(Encoding.UTF8.GetString(body));
        }

        private static async Task<NamedPipeClientStream> ConnectAsync(string name)
        {
            NamedPipeClientStream pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000);
            return pipe;
        }

        [Fact]
        public async Task AnUnchangedSnapshotIsRepeatedOnceASecondWithTheSameRevisionAndAFreshTime()
        {
            string name = UniqueName();
            await using OverlayServer server = new OverlayServer(name);
            server.Start();

            server.Publish(Snapshot("Leather Cap", 10_000));

            await using NamedPipeClientStream pipe = await ConnectAsync(name);
            OverlayState first = await ReadFrameAsync(pipe);
            Assert.Equal(1L, first.Revision);
            Assert.Equal(10_000L, first.PublishedMs);

            // Same session, a second later: nothing changed, but the clock has moved on
            // and the overlay needs to hear that.
            Assert.False(server.Publish(Snapshot("Leather Cap", 11_000)));

            OverlayState heartbeat = await ReadFrameAsync(pipe);
            Assert.Equal(1L, heartbeat.Revision);
            Assert.Equal(11_000L, heartbeat.PublishedMs);
            Assert.Equal("Leather Cap", heartbeat.Panels[0].Rows[0].Cells[0]);
        }

        /// <summary>
        /// The tick is four times a second; the heartbeat is once. A quiet session costs
        /// one small message a second, not four, and the revision does not move.
        /// </summary>
        [Fact]
        public async Task WithinASecondNothingIsSentForAnUnchangedSnapshot()
        {
            string name = UniqueName();
            await using OverlayServer server = new OverlayServer(name);
            server.Start();

            server.Publish(Snapshot("Leather Cap", 10_000));

            await using NamedPipeClientStream pipe = await ConnectAsync(name);
            await ReadFrameAsync(pipe);

            // Three quiet ticks inside the same second.
            Assert.False(server.Publish(Snapshot("Leather Cap", 10_250)));
            Assert.False(server.Publish(Snapshot("Leather Cap", 10_500)));
            Assert.False(server.Publish(Snapshot("Leather Cap", 10_750)));

            // Then the one that crosses the second, which must be the next frame seen -
            // proving the three before it sent nothing.
            Assert.False(server.Publish(Snapshot("Leather Cap", 11_000)));

            OverlayState next = await ReadFrameAsync(pipe);
            Assert.Equal(11_000L, next.PublishedMs);
            Assert.Equal(1L, next.Revision);
        }

        [Fact]
        public async Task AChangeStillMovesTheRevisionAndResetsTheHeartbeat()
        {
            string name = UniqueName();
            await using OverlayServer server = new OverlayServer(name);
            server.Start();

            server.Publish(Snapshot("Leather Cap", 10_000));

            await using NamedPipeClientStream pipe = await ConnectAsync(name);
            await ReadFrameAsync(pipe);

            Assert.True(server.Publish(Snapshot("Short Sword", 10_300)));
            OverlayState changed = await ReadFrameAsync(pipe);
            Assert.Equal(2L, changed.Revision);
            Assert.Equal(10_300L, changed.PublishedMs);

            // The heartbeat clock restarted at the change, so 10_900 is inside the
            // second and sends nothing; 11_300 is the next frame.
            Assert.False(server.Publish(Snapshot("Short Sword", 10_900)));
            Assert.False(server.Publish(Snapshot("Short Sword", 11_300)));

            OverlayState heartbeat = await ReadFrameAsync(pipe);
            Assert.Equal(2L, heartbeat.Revision);
            Assert.Equal(11_300L, heartbeat.PublishedMs);
        }

        [Fact]
        public async Task NoHeartbeatBeforeAnythingHasEverBeenPublished()
        {
            await using OverlayServer server = new OverlayServer(UniqueName());
            server.Start();

            // Never published: there is no snapshot to repeat, and this must not throw.
            OverlayState state = Snapshot("Leather Cap", 50_000);
            Assert.True(server.Publish(state));
            Assert.Equal(1L, server.Revision);
        }
    }
}
