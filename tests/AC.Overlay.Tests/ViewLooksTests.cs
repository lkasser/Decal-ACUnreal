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

        /// <summary>
        /// As VVS's LoadUserSettings: a window the player may resize opens at the size they left it,
        /// kept within what its plugin allows; one they may not is not resized by the store.
        /// </summary>
        [Fact]
        public void AWindowThePlayerMayResizeOpensAtTheSizeTheyLeftIt()
        {
            ViewLooks looks = new ViewLooks("Float", new Dictionary<string, VirindiStoredView>
            {
                ["uTank2:uTank2"] = new VirindiStoredView { Key = "uTank2:uTank2", Theme = "Decal", Width = 856, Height = 360 },
                ["VirindiHUDs:Game Chat"] = new VirindiStoredView { Key = "VirindiHUDs:Game Chat", Width = 4000, Height = 20 },
            });

            DecalView tank = View(ViewBar.Vvs, "uTank2:uTank2");
            tank.Width = 856;
            tank.Height = 210;
            OverlayView resolved = Resolve(looks, tank);
            Assert.Equal((856, 360), (resolved.StoredWidth.Value, resolved.StoredHeight.Value));
            Assert.Equal((856, 210), (resolved.Width, resolved.Height));
            Assert.Equal((100, 100, 1000, 1000), (resolved.MinWidth, resolved.MinHeight, resolved.MaxWidth, resolved.MaxHeight));

            // Within its least and most.
            OverlayView chat = Resolve(looks, View(ViewBar.Vvs, "VirindiHUDs:Game Chat"));
            Assert.Equal((1000, 100), (chat.StoredWidth.Value, chat.StoredHeight.Value));

            DecalView fixedSize = View(ViewBar.Vvs, "uTank2:uTank2");
            fixedSize.Resizeable = false;
            Assert.Null(Resolve(looks, fixedSize).StoredWidth);

            string json = OverlayJson.ToJson(new OverlayState { Windows = { new OverlayWindow { Owner = "VirindiTank", View = resolved } } });
            Assert.Contains("\"stored_width\":856", json);
            Assert.Contains("\"min_width\":100", json);
        }

        /// <summary>
        /// A hudified window goes back against the edges it was left against - VVS's LocSticky, the
        /// left before the right and the top before the bottom - and the plugin's own first place is
        /// where one the store never saw opens, as VVS's view.Location was.
        /// </summary>
        [Fact]
        public void AHudifiedWindowKeepsToTheEdgesItWasLeftAgainstAndOthersOpenWhereThePluginSays()
        {
            ViewLooks looks = new ViewLooks("Float", new Dictionary<string, VirindiStoredView>
            {
                ["VirindiHUDs:StatusHUD"] = new VirindiStoredView { Key = "VirindiHUDs:StatusHUD", Ghosted = true, X = 1840, Y = 1000, Sticky = 2 | 8 },
                ["VirindiHUDs:Comps HUD"] = new VirindiStoredView { Key = "VirindiHUDs:Comps HUD", Ghosted = true, X = 0, Y = 4, Sticky = 1 | 2 | 4 },
                ["uTank2:uTank2"] = new VirindiStoredView { Key = "uTank2:uTank2", Sticky = 1 },
            });

            Assert.Equal("RB", Resolve(looks, View(ViewBar.Vvs, "VirindiHUDs:StatusHUD")).Stuck);
            Assert.Equal("LT", Resolve(looks, View(ViewBar.Vvs, "VirindiHUDs:Comps HUD")).Stuck);

            // Not hudified: VVS put it back nowhere and stuck it to nothing.
            Assert.Null(Resolve(looks, View(ViewBar.Vvs, "uTank2:uTank2")).Stuck);

            // The HSM bar, at (0, 0) until the player moves it, as Virindi HUDs placed it.
            DecalView hsm = View(ViewBar.Vvs, "VirindiHUDs:HSM Bar");
            hsm.Location = (0, 0);
            OverlayView placed = Resolve(looks, hsm);
            Assert.Equal((0, 0), (placed.X.Value, placed.Y.Value));
        }

        /// <summary>
        /// VVS's bar as the store left it: its own row, hudified, for where and against which edges,
        /// and ExtraInfo's VVSBarHorizontal; nothing at all when the store has neither, as on the
        /// player's machine.
        /// </summary>
        [Fact]
        public void TheVvsBarComesBackWhereVvsLeftItAndAcrossIfItSaidSo()
        {
            ViewLooks looks = new ViewLooks("Float", new Dictionary<string, VirindiStoredView>
            {
                [VirindiViewStore.BarKey] = new VirindiStoredView { Key = VirindiViewStore.BarKey, Ghosted = true, X = 0, Y = 300, Sticky = 1 },
            }, new Dictionary<string, long> { ["VVSBarHorizontal"] = 1 });

            OverlayVvsBar bar = looks.VvsBar;
            Assert.Equal((0, 300, "L", true), (bar.X.Value, bar.Y.Value, bar.Stuck, bar.Horizontal.Value));

            string json = OverlayJson.ToJson(new OverlayState { VvsBar = bar });
            Assert.Contains("\"vvs_bar\":{\"x\":0,\"y\":300,\"stuck\":\"L\",\"horizontal\":true}", json);

            Assert.Null(PlayersStore().VvsBar);
            Assert.DoesNotContain("vvs_bar", OverlayJson.ToJson(new OverlayState()));

            // Only the orientation: the bar stays where VVS first put it.
            OverlayVvsBar across = new ViewLooks("Float", null, new Dictionary<string, long> { ["VVSBarHorizontal"] = 0 }).VvsBar;
            Assert.Equal((false, (int?)null), (across.Horizontal.Value, across.X));
        }

        /// <summary>Decal's bar from its registry values, BarAlpha and ViewAlpha with them, as Inject.dll read them.</summary>
        [Fact]
        public void DecalsBarCarriesItsAlphaAndItsViewsAlpha()
        {
            OverlayDecalBar bar = OverlayDecalBar.FromValues(1, 0, 4, 114, 255, 255);
            Assert.Equal((1, 0, 4, 114, 255, 255), (bar.State, bar.Dock, bar.Start, bar.Length, bar.Alpha, bar.ViewAlpha));

            OverlayDecalBar odd = OverlayDecalBar.FromValues(7, 9, -5, 40, 400, -1);
            Assert.Equal((0, 2, 0, 112, 255, 0), (odd.State, odd.Dock, odd.Start, odd.Length, odd.Alpha, odd.ViewAlpha));

            string json = OverlayJson.ToJson(new OverlayState { DecalBar = OverlayDecalBar.FromValues(1, 0, 4, 114, 128, 200) });
            Assert.Contains("\"alpha\":128", json);
            Assert.Contains("\"view_alpha\":200", json);
        }
    }
}
