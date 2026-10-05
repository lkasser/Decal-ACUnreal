using System;
using System.Text;
using System.Threading.Tasks;
using AC.Host.Overlay;
using Xunit;

namespace AC.Overlay.Tests
{
    /// <summary>
    /// Images travel differently from snapshots: every one is needed, so none may be
    /// coalesced away, and each goes once per connection rather than with every snapshot.
    /// These check both halves of that, through a real pipe.
    /// </summary>
    public class OverlayImageTests
    {
        private static string UniqueName() => "achost-overlay-test-" + Guid.NewGuid().ToString("N");

        private static OverlayImage Image(string key, byte shade = 10)
            => new OverlayImage
            {
                Key = key,
                Width = 1,
                Height = 1,
                Rgba = Convert.ToBase64String(new byte[] { shade, shade, shade, 255 }),
            };

        private static OverlayState Snapshot(string character)
            => new OverlayState { Status = new OverlayStatus { Character = character } };

        private static string Kind(byte[] frame, out string detail)
        {
            string json = Encoding.UTF8.GetString(frame);
            if (json.StartsWith("{\"image\"", StringComparison.Ordinal))
            {
                detail = OverlayJson.ReadImage(json).Key;
                return "image";
            }

            detail = OverlayJson.ReadState(json).Status.Character;
            return "state";
        }

        [Fact]
        public async Task ALateOverlayGetsEveryImageBeforeTheSnapshotThatMayNameThem()
        {
            await using OverlayServer server = new OverlayServer(UniqueName());
            Assert.True(server.PublishImage(Image("portal:0600126F")));
            Assert.True(server.PublishImage(Image("portal:0600191A")));
            server.Publish(Snapshot("Frostfell"));
            server.Start();

            await using FakeOverlay overlay = await FakeOverlay.ConnectAsync(server);

            Assert.Equal("image", Kind(await overlay.ReadFrameAsync(), out string first));
            Assert.Equal("portal:0600126F", first);
            Assert.Equal("image", Kind(await overlay.ReadFrameAsync(), out string second));
            Assert.Equal("portal:0600191A", second);
            Assert.Equal("state", Kind(await overlay.ReadFrameAsync(), out string character));
            Assert.Equal("Frostfell", character);
        }

        /// <summary>
        /// The snapshot channel holds one frame and drops the older of two, and an image's
        /// wake-up goes through it. A snapshot it displaced must still arrive.
        /// </summary>
        [Fact]
        public async Task AnImagePublishedWhileAttachedArrivesAndDoesNotCostASnapshot()
        {
            await using OverlayServer server = new OverlayServer(UniqueName());
            server.Start();
            await using FakeOverlay overlay = await FakeOverlay.ConnectAsync(server);

            server.Publish(Snapshot("First"));
            Assert.Equal("state", Kind(await overlay.ReadFrameAsync(), out string first));
            Assert.Equal("First", first);

            server.Publish(Snapshot("Second"));
            server.PublishImage(Image("vvs:Decal_Theme_Images.TabActiveLeft.png"));

            // Either order is right, but both must come.
            bool sawImage = false;
            bool sawSecond = false;
            for (int i = 0; i < 2; i++)
            {
                string kind = Kind(await overlay.ReadFrameAsync(), out string detail);
                sawImage |= kind == "image" && detail == "vvs:Decal_Theme_Images.TabActiveLeft.png";
                sawSecond |= kind == "state" && detail == "Second";
            }

            Assert.True(sawImage);
            Assert.True(sawSecond);
        }

        [Fact]
        public async Task ReplacingAnImageSendsOnlyTheLatestToANewOverlay()
        {
            await using OverlayServer server = new OverlayServer(UniqueName());
            server.PublishImage(Image("bar:open", shade: 1));
            server.PublishImage(Image("bar:open", shade: 2));
            server.Publish(Snapshot("Frostfell"));
            server.Start();

            Assert.Equal(1, server.ImageCount);

            await using FakeOverlay overlay = await FakeOverlay.ConnectAsync(server);
            byte[] frame = await overlay.ReadFrameAsync();
            OverlayImage image = OverlayJson.ReadImage(Encoding.UTF8.GetString(frame));
            Assert.Equal("bar:open", image.Key);
            Assert.Equal(2, Convert.FromBase64String(image.Rgba)[0]);

            Assert.Equal("state", Kind(await overlay.ReadFrameAsync(), out _));
        }

        /// <summary>
        /// The overlay hangs up on a message over a megabyte, taking every panel with it, so
        /// an image that big is refused here instead.
        /// </summary>
        [Fact]
        public async Task AnImageTooLargeForTheOverlayIsRefusedAndSaid()
        {
            string reported = null;
            await using OverlayServer server = new OverlayServer(UniqueName(), (message, _) => reported = message);

            OverlayImage huge = new OverlayImage
            {
                Key = "portal:06FFFFFF",
                Width = 1024,
                Height = 1024,
                Rgba = Convert.ToBase64String(new byte[1024 * 1024 * 4]),
            };

            Assert.False(server.PublishImage(huge));
            Assert.Equal(0, server.ImageCount);
            Assert.Contains("portal:06FFFFFF", reported);
        }

        [Fact]
        public void TheImageFrameIsTheShapeTheOverlayParses()
        {
            string json = OverlayJson.ToJson(Image("portal:0600126F"));

            // Read by ParseFrame and ReadImage in native/ACUnrealOverlay/overlay_ipc.cpp: an
            // object whose member "image" holds key, width, height and base64 rgba.
            Assert.StartsWith("{\"image\":{", json);
            foreach (string key in new[] { "\"key\"", "\"width\"", "\"height\"", "\"rgba\"" })
                Assert.Contains(key, json);
        }
    }
}
