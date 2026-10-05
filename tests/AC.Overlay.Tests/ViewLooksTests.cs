using System.Collections.Generic;
using AC.Dat;
using AC.Host.Overlay;
using AC.Host.Plugins.Views;
using Xunit;

namespace AC.Overlay.Tests
{
    /// <summary>How each window first looks, by Virindi View Service's rules.</summary>
    public class ViewLooksTests
    {
        private const string Xml = "<view icon=\"0\" title=\"T\" width=\"100\" height=\"50\"><control progid=\"DecalControls.FixedLayout\" clipped=\"\"/></view>";

        private static DecalView View(ViewBar bar, string storedKey = null, string theme = null)
        {
            DecalView view = DecalView.Parse(Xml);
            view.Bar = bar;
            view.StoredKey = storedKey;
            view.Theme = theme;
            return view;
        }

        private static OverlayView Resolve(ViewLooks looks, DecalView view)
        {
            OverlayView dto = OverlayMapping.ToDto(view);
            looks.Apply(dto, view);
            return dto;
        }

        private static ViewLooks PlayersStore() => new ViewLooks("Float", new Dictionary<string, VirindiStoredView>
        {
            ["uTank2:uTank2"] = new VirindiStoredView { Key = "uTank2:uTank2", Theme = "Decal", X = 50, Y = 50 },
            ["VirindiHUDs:VT MiniRemote"] = new VirindiStoredView { Key = "VirindiHUDs:VT MiniRemote", Ghosted = true, ClickThrough = true, X = 230, Y = 41 },
        });

        [Fact]
        public void AWindowNobodyPickedAThemeForIsInVvssDefaultOrDecalsOwn()
        {
            ViewLooks looks = PlayersStore();

            // A VVS window with no theme of its own follows VVS's primary theme, which goes to
            // the overlay once, as the default, rather than baked into each window.
            Assert.Equal(string.Empty, Resolve(looks, View(ViewBar.Vvs)).Theme);
            Assert.Equal("Float", looks.DefaultTheme);
            Assert.Equal("Decal", Resolve(looks, View(ViewBar.Decal)).Theme);
            Assert.Equal("Decal", Resolve(looks, View(ViewBar.Vvs, theme: "Decal")).Theme);
        }

        [Fact]
        public void ThePlayersOwnPickOutranksThePlugins()
        {
            OverlayView tank = Resolve(PlayersStore(), View(ViewBar.Vvs, "uTank2:uTank2", theme: "Float"));

            Assert.Equal("Decal", tank.Theme);
            Assert.False(tank.Ghosted);

            // Not hudified: VVS left placing it to the plugin, and so does this.
            Assert.Null(tank.X);
            Assert.Null(tank.Y);
        }

        [Fact]
        public void AHudifiedWindowComesBackHudifiedWhereItWasLeft()
        {
            OverlayView remote = Resolve(PlayersStore(), View(ViewBar.Vvs, "VirindiHUDs:VT MiniRemote"));

            Assert.Equal(string.Empty, remote.Theme);
            Assert.True(remote.Ghosted);
            Assert.True(remote.ClickThrough);
            Assert.Equal(230, remote.X);
            Assert.Equal(41, remote.Y);

            // A window its plugin will not let be hudified is not, whatever the store says.
            DecalView fixedInPlace = View(ViewBar.Vvs, "VirindiHUDs:VT MiniRemote");
            fixedInPlace.Ghostable = false;
            fixedInPlace.ClickThroughable = false;
            OverlayView refused = Resolve(PlayersStore(), fixedInPlace);
            Assert.False(refused.Ghosted);
            Assert.False(refused.ClickThrough);
            Assert.Null(refused.X);
        }

        /// <summary>
        /// VVS's bar order, against the hashes .NET Framework gave these names in the 32-bit
        /// client - worked out from the installed assemblies' own full names.
        /// </summary>
        [Theory]
        [InlineData("uTank2, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null", 204573080)]
        [InlineData("VirindiHUDs, Version=1.0.0.18, Culture=neutral, PublicKeyToken=null", -456600296)]
        [InlineData("VirindiHotkeySystem, Version=1.0.0.6, Culture=neutral, PublicKeyToken=null", 802441897)]
        [InlineData("GoArrow, Version=2.5.0.0, Culture=neutral, PublicKeyToken=null", -2074486636)]
        [InlineData("VCS5, Version=5.0.0.5, Culture=neutral, PublicKeyToken=null", -1059215848)]
        public void VvssBarOrderIsTheAssemblyNamesOwnHash(string fullName, int expected)
        {
            Assert.Equal(expected, ViewBarOrder.Of(fullName));
        }

        [Fact]
        public void AHudsOwnTitleButtonsCrossThePipeAndAPressReachesThem()
        {
            DecalView view = View(ViewBar.Vvs);
            view.ShowInBar = false;
            view.Minimizable = false;
            view.AlphaChangeable = false;
            view.IconKey = "color:FFEE82EE";
            ViewTitleButton add = new ViewTitleButton("add", "host:plusicon", tooltip: "Add the selected item");
            view.TitleButtons.Add(add);
            int pressed = 0;
            add.Pressed += (_, _) => pressed++;

            OverlayView dto = OverlayMapping.ToDto(view);
            Assert.False(dto.ShowInBar);
            Assert.False(dto.Minimizable);
            Assert.False(dto.AlphaChangeable);
            OverlayTitleButton sent = Assert.Single(dto.TitleButtons);
            Assert.Equal(("add", "host:plusicon", "host:plusicon"), (sent.Name, sent.Image, sent.ImageDown));

            string json = OverlayJson.ToJson(new OverlayState { Windows = { new OverlayWindow { Owner = "VirindiTank/comps", View = dto } } });
            Assert.Contains("\"show_in_bar\":false", json);
            Assert.Contains("\"title_buttons\":[{\"name\":\"add\"", json);

            Assert.True(view.Apply(new AC.Host.Plugins.OverlayCommand(ViewVerbs.Press, string.Empty, string.Empty, "add")));
            Assert.Equal(1, pressed);

            // Only pressed: any other verb naming it is refused, and so is a name it lacks.
            Assert.False(view.Apply(new AC.Host.Plugins.OverlayCommand(ViewVerbs.Set, "true", string.Empty, "add")));
            Assert.False(view.Apply(new AC.Host.Plugins.OverlayCommand(ViewVerbs.Press, string.Empty, string.Empty, "nothing")));
            Assert.Equal(1, pressed);
        }

        [Fact]
        public void TheLookCrossesThePipeAndAPositionOnlyWhenThereIsOne()
        {
            OverlayState state = new OverlayState();
            state.Windows.Add(new OverlayWindow { Owner = "A", View = Resolve(PlayersStore(), View(ViewBar.Vvs, "VirindiHUDs:VT MiniRemote")) });
            state.Windows.Add(new OverlayWindow { Owner = "B", View = Resolve(PlayersStore(), View(ViewBar.Vvs)) });

            string json = OverlayJson.ToJson(state);
            Assert.Contains("\"theme\":\"\"", json);
            Assert.Contains("\"ghosted\":true", json);
            Assert.Contains("\"click_through\":true", json);
            Assert.Contains("\"resizeable\":true", json);
            Assert.Contains("\"x\":230", json);

            OverlayState back = OverlayJson.ReadState(json);
            Assert.Equal(230, back.Windows[0].View.X);
            Assert.Null(back.Windows[1].View.X);
        }
    }
}
