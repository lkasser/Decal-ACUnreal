using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AC.Host.Decoding;
using AC.Host.Plugins;
using AC.Host.Transport;
using AC.Proxy;
using Xunit;

namespace AC.Host.Tests
{
    /// <summary>
    /// Plugins contributing panels to the overlay. The point of the mechanism is that a
    /// plugin can put a table on screen while knowing nothing about how anything is drawn,
    /// so what is worth testing is the collection and the isolation - not the appearance,
    /// which lives in a C++ process and cannot be asserted from here.
    /// </summary>
    public class OverlayPanelTests
    {
        private sealed class PanelPlugin : IPlugin, IOverlayPanels
        {
            private readonly Func<IReadOnlyList<OverlayPanel>> _panels;

            public PanelPlugin(string name, Func<IReadOnlyList<OverlayPanel>> panels)
            {
                Name = name;
                _panels = panels;
            }

            public string Name { get; }

            public int Asked { get; private set; }

            public void Startup(IHost host)
            {
            }

            public void Shutdown()
            {
            }

            public IReadOnlyList<OverlayPanel> GetPanels()
            {
                Asked++;
                return _panels();
            }
        }

        private sealed class QuietPlugin : IPlugin
        {
            public string Name => "Quiet";

            public void Startup(IHost host)
            {
            }

            public void Shutdown()
            {
            }
        }

        private static async Task<GameHost> RunAsync(params IPlugin[] plugins)
        {
            GameHost host = new GameHost(
                new CaptureTransport(Array.Empty<CapturedDatagram>()),
                new ListLog(),
                dataRoot: Path.Combine(Path.GetTempPath(), "achost-tests"));

            foreach (IPlugin plugin in plugins)
                host.AddPlugin(plugin);

            await host.StartAsync();
            await host.Ended.WaitAsync(TimeSpan.FromSeconds(10));
            return host;
        }

        private static OverlayPanel Panel(string title)
            => new OverlayPanel(title, new[] { "A" }, new[] { new OverlayRow("one") });

        [Fact]
        public async Task PanelsArriveInPluginOrder()
        {
            PanelPlugin first = new PanelPlugin("First", () => new[] { Panel("Alpha") });
            PanelPlugin second = new PanelPlugin("Second", () => new[] { Panel("Beta"), Panel("Gamma") });

            await using GameHost host = await RunAsync(first, second);

            IReadOnlyList<OverlayPanel> panels = host.CollectPanels();

            Assert.Equal(new[] { "Alpha", "Beta", "Gamma" }, panels.Select(p => p.Title));
        }

        [Fact]
        public async Task APluginWithNothingToShowIsNotAsked()
        {
            QuietPlugin quiet = new QuietPlugin();

            await using GameHost host = await RunAsync(quiet);

            Assert.Empty(host.CollectPanels());
        }

        /// <summary>
        /// One plugin's broken panel must cost the overlay one table, not the session -
        /// the same rule every other plugin callback follows.
        /// </summary>
        [Fact]
        public async Task APluginThatThrowsIsSkippedAndTheOthersStillShow()
        {
            PanelPlugin bad = new PanelPlugin("Bad", () => throw new InvalidOperationException("no"));
            PanelPlugin good = new PanelPlugin("Good", () => new[] { Panel("Survivor") });

            await using GameHost host = await RunAsync(bad, good);

            IReadOnlyList<OverlayPanel> panels = host.CollectPanels();

            OverlayPanel panel = Assert.Single(panels);
            Assert.Equal("Survivor", panel.Title);
            Assert.Equal(1, host.Statistics.PluginExceptions);
        }

        [Fact]
        public async Task ANullListAndANullPanelAreBothTolerated()
        {
            PanelPlugin nothing = new PanelPlugin("Nothing", () => null);
            PanelPlugin holes = new PanelPlugin("Holes", () => new OverlayPanel[] { null, Panel("Real"), null });

            await using GameHost host = await RunAsync(nothing, holes);

            OverlayPanel panel = Assert.Single(host.CollectPanels());
            Assert.Equal("Real", panel.Title);
            Assert.Equal(0, host.Statistics.PluginExceptions);
        }

        [Fact]
        public async Task CollectingTwiceAsksTwiceBecauseTheAnswerCanChange()
        {
            int calls = 0;
            PanelPlugin plugin = new PanelPlugin("Counting", () => new[] { Panel("Call " + ++calls) });

            await using GameHost host = await RunAsync(plugin);

            Assert.Equal("Call 1", host.CollectPanels()[0].Title);
            Assert.Equal("Call 2", host.CollectPanels()[0].Title);
            Assert.Equal(2, plugin.Asked);
        }

        /// <summary>
        /// Panels are grouped into one window per plugin on the strength of the owner, so
        /// it is stamped by the host. A plugin cannot put a panel in another plugin's
        /// window by naming it.
        /// </summary>
        [Fact]
        public async Task EveryPanelIsStampedWithThePluginThatProducedIt()
        {
            PanelPlugin first = new PanelPlugin("First", () => new[] { Panel("Alpha") });
            PanelPlugin second = new PanelPlugin("Second", () => new[] { Panel("Beta") });

            await using GameHost host = await RunAsync(first, second);

            IReadOnlyList<OverlayPanel> panels = host.CollectPanels();

            Assert.Equal(new[] { "First", "Second" }, panels.Select(p => p.Owner));
        }

        [Fact]
        public async Task APluginCannotClaimAnotherPluginsWindow()
        {
            // A panel that arrives already claiming an owner is re-stamped regardless.
            OverlayPanel forged = Panel("Forged");
            forged.Owner = "SomebodyElse";

            PanelPlugin honest = new PanelPlugin("Honest", () => new[] { forged });

            await using GameHost host = await RunAsync(honest);

            OverlayPanel panel = Assert.Single(host.CollectPanels());
            Assert.Equal("Honest", panel.Owner);
        }

        [Fact]
        public void ARowMadeWithoutAToneIsNormal()
        {
            OverlayRow row = new OverlayRow("a", "b");

            Assert.Equal(OverlayTone.Normal, row.Tone);
            Assert.Equal(new[] { "a", "b" }, row.Cells);
        }

        [Fact]
        public void APanelBuiltFromNullsIsEmptyRatherThanBroken()
        {
            // The host publishes whatever a plugin returns, and the drawing code is in
            // another process: a null here would become a crash there.
            OverlayPanel panel = new OverlayPanel(null, null, null);

            Assert.Equal(string.Empty, panel.Title);
            Assert.Empty(panel.Columns);
            Assert.Empty(panel.Rows);
        }
    }
}
