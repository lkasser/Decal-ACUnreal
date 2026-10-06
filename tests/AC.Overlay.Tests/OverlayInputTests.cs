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
        /// A click rides in the input frame, as overlay_ipc.cpp reads it: an id, the layout's size,
        /// and the points as [x, y]. A frame without one says nothing of clicks at all.
        /// </summary>
        [Fact]
        public void AClickTravelsInTheInputFrame()
        {
            OverlayInput input = new OverlayInput
            {
                Sequence = 4,
                Click = new OverlayClick { Id = 1759700000123, LayoutWidth = 800, LayoutHeight = 600, Points = { new[] { 122, 220 }, new[] { 344, 394 } } },
            };

            string json = OverlayJson.ToJson(input);
            Assert.Equal("{\"input\":{\"held\":[],\"sequence\":4,\"click\":{\"id\":1759700000123,\"layout_width\":800,\"layout_height\":600,\"points\":[[122,220],[344,394]]}}}", json);

            OverlayClick read = OverlayJson.ReadInput(json).Click;
            Assert.Equal((1759700000123L, 800, 600), (read.Id, read.LayoutWidth, read.LayoutHeight));
            Assert.Equal(new[] { 344, 394 }, read.Points[1]);
            Assert.DoesNotContain("click", OverlayJson.ToJson(new OverlayInput { Held = { 0x57 } }));
        }

        /// <summary>
        /// A click on a layout the client draws at its Desktop UI Scale carries the scale the player
        /// chose; one at the layout's own size says nothing of a scale, as before there was one.
        /// </summary>
        [Fact]
        public void AClickCarriesTheClientsUiScale()
        {
            OverlayInput input = new OverlayInput
            {
                Sequence = 5,
                Click = new OverlayClick { Id = 1759700000124, LayoutWidth = 800, LayoutHeight = 600, Points = { new[] { 344, 394 } }, UiScale = 1.75 },
            };

            string json = OverlayJson.ToJson(input);
            Assert.Equal("{\"input\":{\"held\":[],\"sequence\":5,\"click\":{\"id\":1759700000124,\"layout_width\":800,\"layout_height\":600,\"points\":[[344,394]],\"ui_scale\":1.75}}}", json);
            Assert.Equal(1.75, OverlayJson.ReadInput(json).Click.UiScale);
        }

        /// <summary>
        /// The client's own plugin bar, and the scale it draws at, ride in the snapshot as
        /// overlay_ipc.cpp reads them: [x, y, width, height] in its interface units. Absent when the
        /// client's settings were not read.
        /// </summary>
        [Fact]
        public void TheClientsPluginBarTravelsInTheSnapshot()
        {
            OverlayState state = new OverlayState { ClientUi = new OverlayClientUi { UiScale = 1.5, PluginBar = new[] { 8.0, 80, 62, 214 } } };

            string json = OverlayJson.ToJson(state);
            Assert.Contains("\"client_ui\":{\"ui_scale\":1.5,\"plugin_bar\":[8,80,62,214]}", json);
            Assert.DoesNotContain("client_ui", OverlayJson.ToJson(new OverlayState()));
        }

        /// <summary>
        /// A click goes out with the keys held now, and with each change of them for a moment after,
        /// so a frame lost cannot lose it; its id is new each time - never one a host before it used
        /// - which is what the overlay makes it once by.
        /// </summary>
        [Fact]
        public async Task AClickGoesOutWithTheKeysAndEachHasANewId()
        {
            await using OverlayServer server = new OverlayServer(UniqueName());
            server.Start();
            await using FakeOverlay overlay = await FakeOverlay.ConnectAsync(server);
            server.Publish(new OverlayState { Status = new OverlayStatus { Character = "First" } });
            await overlay.ReadFrameAsync();

            server.PublishInput(new[] { 0x57 });
            long first = server.PublishClick(new OverlayClick { LayoutWidth = 800, LayoutHeight = 600, Points = { new[] { 344, 394 } } });
            Assert.True(first >= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 60000);

            OverlayInput sent = await NextInputAsync(overlay, i => i.Click != null);
            Assert.Equal(first, sent.Click.Id);
            Assert.Equal(new[] { 0x57 }, sent.Held);

            // A change of keys straight after still carries it.
            server.PublishInput(Array.Empty<int>());
            sent = await NextInputAsync(overlay, i => i.Held.Count == 0);
            Assert.Equal(first, sent.Click?.Id);

            long second = server.PublishClick(new OverlayClick { LayoutWidth = 800, LayoutHeight = 600, Points = { new[] { 122, 220 } } });
            Assert.True(second > first);
            Assert.Equal(second, (await NextInputAsync(overlay, i => i.Click?.Id == second)).Click.Id);
        }

        private static async Task<OverlayInput> NextInputAsync(FakeOverlay overlay, Func<OverlayInput, bool> wanted)
        {
            for (int i = 0; i < 20; i++)
            {
                string json = Encoding.UTF8.GetString(await overlay.ReadFrameAsync());
                if (!json.StartsWith("{\"input\"", StringComparison.Ordinal))
                    continue;

                OverlayInput input = OverlayJson.ReadInput(json);
                if (wanted(input))
                    return input;
            }

            throw new Xunit.Sdk.XunitException("The input frame wanted never came.");
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

        /// <summary>
        /// Whether minimizing parks the game travels in every snapshot, off unless the player asked:
        /// the overlay reads it when the player minimizes, so a change applies at once.
        /// </summary>
        [Fact]
        public void KeepingOnWhileMinimizedTravelsInTheSnapshotAndIsOffUnlessAsked()
        {
            string off = OverlayJson.ToJson(new OverlayState());
            Assert.Contains("\"keep_playing_minimized\":false", off);
            Assert.False(OverlayJson.ReadState(off).KeepPlayingMinimized);

            string on = OverlayJson.ToJson(new OverlayState { KeepPlayingMinimized = true });
            Assert.Contains("\"keep_playing_minimized\":true", on);
            Assert.True(OverlayJson.ReadState(on).KeepPlayingMinimized);

            // An older host's snapshot, without it, reads as off.
            Assert.False(OverlayJson.ReadState("{\"status\":{}}").KeepPlayingMinimized);
        }

        /// <summary>
        /// The overlay says what the game window is doing as a command of the host's own - no
        /// owner - named "game-window", with "minimized,drawing,parked".
        /// </summary>
        [Fact]
        public async Task TheOverlaysWordOnTheGameWindowArrivesAsTheHostsOwnCommand()
        {
            await using OverlayServer server = new OverlayServer(UniqueName());
            TaskCompletionSource<OverlayCommand> heard = new TaskCompletionSource<OverlayCommand>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.CommandReceived += (_, command) => heard.TrySetResult(command);
            server.Start();

            await using FakeOverlay overlay = await FakeOverlay.ConnectAsync(server);
            await overlay.SendAsync(new OverlayCommand { Name = "game-window", Value = "1,0,0" });

            OverlayCommand command = await heard.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("game-window", command.Name);
            Assert.Equal("1,0,0", command.Value);
            Assert.Empty(command.Owner);
        }
    }
}
