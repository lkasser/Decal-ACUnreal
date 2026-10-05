using System;
using System.Text;
using System.Threading.Tasks;
using AC.Host.Overlay;
using Xunit;

namespace AC.Overlay.Tests
{
    /// <summary>
    /// Keys the host asks the overlay to hold down in the game. Always the whole set, so a
    /// frame lost or repeated cannot leave a key held that the host has let go.
    /// </summary>
    public class OverlayInputTests
    {
        private static string UniqueName() => "achost-overlay-test-" + Guid.NewGuid().ToString("N");

        [Fact]
        public void TheFrameIsAnObjectWithOneInputMemberHoldingTheWholeSet()
        {
            string json = OverlayJson.ToJson(new OverlayInput { Held = { 0x57, 0x41 }, Sequence = 3 });

            Assert.Equal("{\"input\":{\"held\":[87,65],\"sequence\":3}}", json);
            Assert.Equal(new[] { 0x57, 0x41 }, OverlayJson.ReadInput(json).Held);
        }

        [Fact]
        public async Task KeysPublishedWhileAttachedArriveAndSoDoesTheSnapshotBesideThem()
        {
            await using OverlayServer server = new OverlayServer(UniqueName());
            server.Start();
            await using FakeOverlay overlay = await FakeOverlay.ConnectAsync(server);

            server.Publish(new OverlayState { Status = new OverlayStatus { Character = "First" } });
            Assert.StartsWith("{\"status\"", Encoding.UTF8.GetString(await overlay.ReadFrameAsync()), StringComparison.Ordinal);

            server.Publish(new OverlayState { Status = new OverlayStatus { Character = "Second" } });
            server.PublishInput(new[] { 0x57 });

            bool sawInput = false;
            bool sawSecond = false;
            for (int i = 0; i < 2; i++)
            {
                string json = Encoding.UTF8.GetString(await overlay.ReadFrameAsync());
                if (json.StartsWith("{\"input\"", StringComparison.Ordinal))
                {
                    sawInput = true;
                    Assert.Equal(new[] { 0x57 }, OverlayJson.ReadInput(json).Held);
                }
                else
                {
                    sawSecond |= OverlayJson.ReadState(json).Status.Character == "Second";
                }
            }

            Assert.True(sawInput);
            Assert.True(sawSecond);
        }

        /// <summary>
        /// A key held for one overlay is not pressed for the next: a reinjected overlay starts
        /// with nothing held until the host repeats itself.
        /// </summary>
        [Fact]
        public async Task ANewOverlayIsNotSentKeysHeldForTheOldOne()
        {
            await using OverlayServer server = new OverlayServer(UniqueName());
            server.PublishInput(new[] { 0x57 });
            server.Publish(new OverlayState { Status = new OverlayStatus { Character = "Only" } });
            server.Start();

            await using FakeOverlay overlay = await FakeOverlay.ConnectAsync(server);
            string first = Encoding.UTF8.GetString(await overlay.ReadFrameAsync());
            Assert.StartsWith("{\"status\"", first, StringComparison.Ordinal);

            server.PublishInput(Array.Empty<int>());
            string next = Encoding.UTF8.GetString(await overlay.ReadFrameAsync());
            Assert.Empty(OverlayJson.ReadInput(next).Held);
        }
    }
}
