using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AC.Host.Plugins;
using AC.Host.Plugins.Views;
using Xunit;

namespace AC.Host.Tests
{
    /// <summary>
    /// Views written for Decal, for these tests, with one of every control the host draws. The first
    /// is laid out as a plugin's main window with a notebook is - control names of the kind Virindi
    /// Tank's views use, on a layout and with labels of this repository's own.
    /// </summary>
    internal static class SampleViews
    {
        public const string Tank = @"<?xml version=""1.0""?>
<view icon=""100"" title=""uTank2"" width=""600"" height=""220"">
  <control progid=""DecalControls.Notebook"" name=""MainTabs"">
    <page label=""Options"">
      <control progid=""DecalControls.FixedLayout"" clipped="""">
        <control progid=""DecalControls.StaticText"" name=""Label12"" left=""6"" top=""6"" width=""70"" height=""16"" text=""Range:""/>
        <control progid=""DecalControls.Edit"" name=""txtRange"" left=""80"" top=""6"" width=""50"" height=""16"" imageportalsrc=""4726"" text=""35""/>
        <control progid=""DecalControls.PushButton"" name=""bForceBuff"" left=""140"" top=""4"" width=""100"" height=""22"" text=""Buff now""/>
        <control progid=""DecalControls.Checkbox"" name=""cEnableBuffing"" left=""6"" top=""34"" width=""110"" height=""18"" text=""Buffing""/>
        <control progid=""DecalControls.Checkbox"" name=""cExact"" left=""120"" top=""34"" width=""60"" height=""18"" text=""Exact"" checked=""true""/>
        <control progid=""DecalControls.Progress"" name=""prgBuffs"" left=""8"" top=""150"" width=""200"" height=""12"" value=""25"" maxvalue=""50""/>
      </control>
    </page>
    <page label=""Route"">
      <control progid=""DecalControls.FixedLayout"" clipped="""">
        <!-- Comments are in the real files, and must not trip the parser. -->
        <control progid=""DecalControls.List"" name=""lstMonsters"" left=""6"" top=""6"" width=""400"" height=""100"">
          <column progid=""DecalControls.CheckColumn"" name=""clFester"" fixedwidth=""16""/>
          <column progid=""DecalControls.TextColumn"" name=""clName"" fixedwidth=""200""/>
          <column progid=""DecalControls.IconColumn"" name=""clMoveUp"" fixedwidth=""16""/>
          <column progid=""DecalControls.TextColumn"" name=""clSetting"" />
        </control>
        <control progid=""DecalControls.FixedLayout"" clipped="""" name=""layoutBottom"" left=""6"" top=""112"" width=""300"" height=""20"">
          <control progid=""DecalControls.Choice"" name=""cmbNavType"" left=""0"" top=""0"" width=""90"" height=""18"" selected=""1"">
            <option text=""Circular"" data=""c""/>
            <option text=""Linear"" data=""""/>
          </control>
          <control progid=""DecalControls.Button"" name=""btnNavDown"" left=""280"" top=""2"" height=""16"" width=""18""/>
        </control>
      </control>
    </page>
    <page label=""Vitals"">
      <control progid=""DecalControls.FixedLayout"" clipped="""">
        <control progid=""DecalControls.Slider"" name=""slMyHP"" left=""30"" top=""6"" width=""150"" height=""16"" minimum=""10"" maximum=""90"" textcolor=""0"" vertical=""0""/>
        <control progid=""DecalControls.StaticText"" name=""lblE"" left=""6"" top=""6"" width=""16"" height=""16"" text=""E"" fontface=""Times New Roman"" fontsize=""16"" fontstyle="" bold"" justify=""Center"" textcolor=""192""/>
      </control>
    </page>
  </control>
</view>";

        /// <summary>
        /// A view with VVS's own picture box and console, which Decal's XML never had and a VVS
        /// plugin made in code - a Comps HUD row and a chat box.
        /// </summary>
        public const string Hud = @"<?xml version=""1.0""?>
<view icon=""0"" title=""Comps HUD"" width=""62"" height=""60"">
  <control progid=""DecalControls.FixedLayout"" clipped="""">
    <control progid=""VirindiViewService.Controls.HudPictureBox"" name=""icon0"" left=""0"" top=""0"" width=""20"" height=""20"" image=""9770""/>
    <control progid=""VirindiViewService.Controls.HudConsole"" name=""chat"" left=""0"" top=""20"" width=""62"" height=""40""/>
  </control>
</view>";

        /// <summary>A view with no notebook, as the smaller Decal plugins had.</summary>
        public const string Flat = @"<?xml version=""1.0""?>
<view icon=""26075"" title=""Example Plugin"" width=""246"" height=""217"">
    <control progid=""DecalControls.FixedLayout"" clipped="""">
        <control progid=""DecalControls.PushButton"" name=""bTest"" left=""128"" top=""128"" width=""104"" height=""32"" text=""Test Button""/>
        <control progid=""DecalControls.Edit"" name=""txtTest"" left=""16"" top=""24"" width=""200"" height=""16"" imageportalsrc=""4726"" text=""Sample text""/>
        <control progid=""DecalControls.Slider"" name=""sldTest"" left=""16"" top=""64"" width=""216"" height=""16"" minimum=""0"" maximum=""100"" textcolor=""0"" vertical=""1""/>
    </control>
</view>";
    }

    /// <summary>
    /// Reading Decal view XML, and applying the player's clicks to what was read. What is
    /// worth pinning down is everything a plugin author would otherwise find out by eye:
    /// Decal's own number formats (COLORREF colours, the low half of portal ids), which
    /// attribute means what, and that a click changes the control before the plugin's
    /// handler runs - because a handler that reads the control is how Decal code was
    /// written, and it must see the new value.
    /// </summary>
    public class DecalViewTests
    {
        private static DecalView Tank() => DecalView.Parse(SampleViews.Tank);

        [Fact]
        public void TheViewsOwnAttributesAreRead()
        {
            DecalView view = Tank();

            Assert.Equal("uTank2", view.Title);
            Assert.Equal("portal:06000064", view.IconKey);
            Assert.Equal(600, view.Width);
            Assert.Equal(220, view.Height);
            Assert.Empty(view.Warnings);

            Notebook root = Assert.IsType<Notebook>(view.Root);
            Assert.Equal("MainTabs", root.Name);
        }

        [Fact]
        public void EveryControlIsKeptInDocumentOrderWithParentsFirst()
        {
            DecalView view = Tank();

            Assert.Equal(
                new[]
                {
                    typeof(Notebook), typeof(FixedLayout), typeof(StaticText), typeof(Edit), typeof(PushButton),
                    typeof(Checkbox), typeof(Checkbox), typeof(Progress),
                    typeof(FixedLayout), typeof(List), typeof(FixedLayout), typeof(Choice), typeof(ImageButton),
                    typeof(FixedLayout), typeof(Slider), typeof(StaticText),
                },
                view.Controls.Select(c => c.GetType()));
        }

        [Fact]
        public void NotebookPagesKeepTheirOrderAndTheFirstIsShowing()
        {
            Notebook tabs = Tank().Notebook("MainTabs");

            Assert.Equal(new[] { "Options", "Route", "Vitals" }, tabs.Pages.Select(p => p.Label));
            Assert.All(tabs.Pages, p => Assert.IsType<FixedLayout>(p.Content));
            Assert.Equal(0, tabs.ActivePage);
        }

        [Fact]
        public void PositionsAndSizesAreReadWhateverOrderTheAttributesComeIn()
        {
            DecalView view = Tank();

            Edit range = view.Edit("txtRange");
            Assert.Equal((80, 6, 50, 16), (range.Left, range.Top, range.Width, range.Height));

            // Height before width in the XML, as Virindi Tank's buttons have it.
            ImageButton down = view.ImageButton("btnNavDown");
            Assert.Equal((280, 2, 18, 16), (down.Left, down.Top, down.Width, down.Height));

            // A nested layout has a place of its own; a page's layout has none.
            FixedLayout bottom = view.Get<FixedLayout>("layoutBottom");
            Assert.Equal((6, 112, 300, 20), (bottom.Left, bottom.Top, bottom.Width, bottom.Height));
            Assert.Equal(new[] { "cmbNavType", "btnNavDown" }, bottom.Children.Select(c => c.Name));

            FixedLayout page = Assert.IsType<FixedLayout>(view.Notebook("MainTabs").Pages[0].Content);
            Assert.Equal(string.Empty, page.Name);
            Assert.Equal((0, 0, 0, 0), (page.Left, page.Top, page.Width, page.Height));
        }

        [Fact]
        public void StaticTextTakesDecalsColourFontAndJustification()
        {
            DecalView view = Tank();

            // 192 is a COLORREF, 0x00BBGGRR: red, not blue.
            StaticText styled = view.StaticText("lblE");
            Assert.Equal("E", styled.Text);
            Assert.Equal(0xFFC00000L, styled.TextColor);
            Assert.Equal(16, styled.FontSize);
            Assert.True(styled.Bold);
            Assert.Equal(ViewJustify.Center, styled.Justify);

            StaticText plain = view.StaticText("Label12");
            Assert.Equal("Range:", plain.Text);
            Assert.Null(plain.TextColor);
            Assert.Equal(0, plain.FontSize);
            Assert.False(plain.Bold);
            Assert.Equal(ViewJustify.Default, plain.Justify);
        }

        [Fact]
        public void EditsCheckboxesAndButtonsTakeTheirTextAndState()
        {
            DecalView view = Tank();

            Edit range = view.Edit("txtRange");
            Assert.Equal("35", range.Text);
            Assert.Equal("portal:06001276", range.ImageKey);

            Assert.True(view.Checkbox("cExact").Checked);
            Assert.Equal("Exact", view.Checkbox("cExact").Text);
            Assert.False(view.Checkbox("cEnableBuffing").Checked);

            Assert.Equal("Buff now", view.PushButton("bForceBuff").Text);
            Assert.Equal(string.Empty, view.ImageButton("btnNavDown").ImageKey);
        }

        [Fact]
        public void AChoiceHasItsOptionsAndStartsOnTheOneSelected()
        {
            Choice nav = Tank().Choice("cmbNavType");

            Assert.Equal(new[] { "Circular", "Linear" }, nav.Options.Select(o => o.Text));
            Assert.Equal(new[] { "c", string.Empty }, nav.Options.Select(o => o.Data));
            Assert.Equal(1, nav.Selected);
            Assert.Equal("Linear", nav.SelectedText);
        }

        [Fact]
        public void SlidersAndProgressBarsHaveTheirRanges()
        {
            DecalView view = Tank();

            Slider hp = view.Slider("slMyHP");
            Assert.Equal(10, hp.Minimum);
            Assert.Equal(90, hp.Maximum);
            Assert.Equal(10, hp.Position);
            Assert.False(hp.Vertical);

            Assert.True(DecalView.Parse(SampleViews.Flat).Slider("sldTest").Vertical);

            Progress buffs = view.Progress("prgBuffs");
            Assert.Equal(0, buffs.Minimum);
            Assert.Equal(50, buffs.Maximum);
            Assert.Equal(25, buffs.Value);
        }

        [Fact]
        public void AListHasItsColumnsAndNoRowsUntilThePluginAddsThem()
        {
            List monsters = Tank().List("lstMonsters");

            Assert.Equal(
                new[] { ListColumnKind.Check, ListColumnKind.Text, ListColumnKind.Icon, ListColumnKind.Text },
                monsters.Columns.Select(c => c.Kind));

            // No fixedwidth means the column shares what the fixed ones leave.
            Assert.Equal(new[] { 16, 200, 16, 0 }, monsters.Columns.Select(c => c.FixedWidth));
            Assert.Equal(new[] { "clFester", "clName", "clMoveUp", "clSetting" }, monsters.Columns.Select(c => c.Name));
            Assert.Equal(0, monsters.RowCount);
        }

        [Fact]
        public void AViewCanBeAFixedLayoutWithNoNotebook()
        {
            DecalView view = DecalView.Parse(SampleViews.Flat);

            FixedLayout root = Assert.IsType<FixedLayout>(view.Root);
            Assert.Equal(3, root.Children.Count);
            Assert.Equal("portal:060065DB", view.IconKey);
            Assert.Equal("Sample text", view.Edit("txtTest").Text);
            Assert.Empty(view.Warnings);
        }

        [Fact]
        public void AnUnknownProgidIsKeptUndrawnAndSaidSo()
        {
            DecalView view = DecalView.Parse(@"<view title=""t""><control progid=""DecalControls.FixedLayout"">
                <control progid=""DecalControls.Scroller"" name=""scrWhat"" left=""4"" top=""5"" width=""6"" height=""7""><control progid=""DecalControls.PushButton"" name=""inner""/></control>
                <control progid=""DecalControls.PushButton"" name=""after""/>
            </control></view>");

            UnknownControl unknown = view.Get<UnknownControl>("scrWhat");
            Assert.Equal("DecalControls.Scroller", unknown.ProgId);
            Assert.Equal(4, unknown.Left);

            // Its contents are not guessed at; what follows it is unaffected.
            Assert.False(view.Contains("inner"));
            Assert.True(view.Contains("after"));

            string warning = Assert.Single(view.Warnings);
            Assert.Contains("DecalControls.Scroller", warning);
            Assert.Contains("scrWhat", warning);
            Assert.StartsWith("Line 2:", warning);
        }

        [Fact]
        public void TwoControlsWithOneNameAreAWarningAndTheFirstIsTheOneFound()
        {
            DecalView view = DecalView.Parse(@"<view><control progid=""DecalControls.FixedLayout"">
                <control progid=""DecalControls.PushButton"" name=""same"" text=""first""/>
                <control progid=""DecalControls.PushButton"" name=""same"" text=""second""/>
            </control></view>");

            Assert.Equal("first", view.PushButton("same").Text);
            Assert.Contains("same", Assert.Single(view.Warnings));
            Assert.Equal(3, view.Controls.Count);
        }

        [Fact]
        public void NumbersThatDoNotParseAreWarningsNotFailures()
        {
            DecalView view = DecalView.Parse(@"<view><control progid=""DecalControls.FixedLayout"">
                <control progid=""DecalControls.Choice"" name=""pick"" selected=""3""><option text=""only""/></control>
                <control progid=""DecalControls.StaticText"" name=""lbl"" left=""wide"" textcolor=""red""/>
            </control></view>");

            Assert.Equal(-1, view.Choice("pick").Selected);
            Assert.Equal(0, view.StaticText("lbl").Left);
            Assert.Null(view.StaticText("lbl").TextColor);
            Assert.Equal(3, view.Warnings.Count);
        }

        [Theory]
        [InlineData("<view><control progid=\"DecalControls.FixedLayout\"></view>")]
        [InlineData("")]
        [InlineData("not xml at all")]
        public void XmlThatIsNotWellFormedIsAFormatException(string xml)
        {
            FormatException thrown = Assert.Throws<FormatException>(() => DecalView.Parse(xml));
            Assert.Contains("not well formed", thrown.Message);
        }

        [Fact]
        public void XmlThatIsNotAViewIsAFormatExceptionSayingWhy()
        {
            Assert.Contains("<panel>", Assert.Throws<FormatException>(() => DecalView.Parse("<panel/>")).Message);
            Assert.Contains("no <control>", Assert.Throws<FormatException>(() => DecalView.Parse("<view title=\"x\"/>")).Message);
            Assert.Throws<ArgumentNullException>(() => DecalView.Parse(null));
        }

        [Fact]
        public void AskingForTheWrongTypeSaysWhatTheControlActuallyIs()
        {
            DecalView view = Tank();

            KeyNotFoundException wrong = Assert.Throws<KeyNotFoundException>(() => view.Checkbox("txtRange"));
            Assert.Contains("'txtRange'", wrong.Message);
            Assert.Contains("is an Edit (DecalControls.Edit)", wrong.Message);
            Assert.Contains("not a Checkbox", wrong.Message);

            Assert.Contains("not an ImageButton", Assert.Throws<KeyNotFoundException>(() => view.ImageButton("bForceBuff")).Message);

            KeyNotFoundException missing = Assert.Throws<KeyNotFoundException>(() => view.Get<Slider>("slNope"));
            Assert.Contains("'slNope'", missing.Message);
            Assert.Contains("uTank2", missing.Message);
        }

        [Fact]
        public void TryGetAnswersWithoutThrowing()
        {
            DecalView view = Tank();

            Assert.True(view.TryGet("cExact", out Checkbox box));
            Assert.Equal("cExact", box.Name);

            Assert.False(view.TryGet("cExact", out Slider wrongType));
            Assert.Null(wrongType);

            Assert.False(view.TryGet("nothing", out ViewControl missing));
            Assert.Null(missing);

            Assert.False(view.TryGet(null, out ViewControl nothing));
            Assert.Null(nothing);
        }

        [Fact]
        public void PortalImagesAndColoursHaveOneSpellingWhicheverWayTheyAreGiven()
        {
            Assert.Equal("portal:06001276", ViewImages.Portal(4726));
            Assert.Equal("portal:06001276", ViewImages.Portal(0x06001276));
            Assert.Equal(string.Empty, ViewImages.Portal(0));

            Assert.Equal(0xFF4080FFL, ViewColor.FromColorRef(0x00FF8040));
            Assert.Equal(0xFF000000L, ViewColor.FromColorRef(0));
            Assert.Equal(0xFF102030L, ViewColor.Rgb(0x10, 0x20, 0x30));

            ImageButton down = Tank().ImageButton("btnNavDown");
            down.SetPortalImage(0x060011F8);
            Assert.Equal("portal:060011F8", down.ImageKey);
        }

        // --- Applying the player's commands -------------------------------------------------

        private static OverlayCommand Set(string control, string value) => new OverlayCommand(ViewVerbs.Set, value, controlId: control);

        [Fact]
        public void ACheckboxIsSetBeforeItsHandlerRuns()
        {
            DecalView view = Tank();
            Checkbox box = view.Checkbox("cEnableBuffing");

            List<(bool seen, bool args, object sender)> raised = new List<(bool, bool, object)>();
            box.Changed += (sender, e) => raised.Add((box.Checked, e.Checked, sender));

            Assert.True(view.Apply(Set("cEnableBuffing", "true")));
            Assert.True(box.Checked);
            Assert.Equal((true, true, (object)box), Assert.Single(raised));

            // Anything but exactly "true" is off.
            Assert.True(view.Apply(Set("cEnableBuffing", "True")));
            Assert.False(box.Checked);
            Assert.Equal((false, false, (object)box), raised[1]);
        }

        [Fact]
        public void AnEditTakesTheCommittedText()
        {
            DecalView view = Tank();
            Edit range = view.Edit("txtRange");

            string seen = null;
            EditChangedEventArgs args = null;
            range.Changed += (_, e) => { seen = range.Text; args = e; };

            Assert.True(view.Apply(Set("txtRange", "40")));
            Assert.Equal("40", range.Text);
            Assert.Equal("40", seen);
            Assert.Equal("40", args.Text);
            Assert.Equal("txtRange", args.Name);
            Assert.Same(range, args.Control);
        }

        [Fact]
        public void AChoiceTakesAnIndexAndRefusesOneItDoesNotHave()
        {
            DecalView view = Tank();
            Choice nav = view.Choice("cmbNavType");

            List<ChoiceChangedEventArgs> raised = new List<ChoiceChangedEventArgs>();
            nav.Changed += (_, e) => raised.Add(e);

            Assert.True(view.Apply(Set("cmbNavType", "0")));
            Assert.Equal(0, nav.Selected);
            ChoiceChangedEventArgs e = Assert.Single(raised);
            Assert.Equal(0, e.Selected);
            Assert.Equal("Circular", e.Text);

            foreach (string bad in new[] { "2", "-1", "Linear", string.Empty, "1.0" })
                Assert.False(view.Apply(Set("cmbNavType", bad)));

            Assert.Single(raised);
            Assert.Equal(0, nav.Selected);
        }

        /// <summary>
        /// The overlay writes numbers invariantly whatever the machine's culture, so they are
        /// read back the same way: under de-DE, "42.5" read naively would be 425.
        /// </summary>
        [Fact]
        public void ASliderReadsAnInvariantNumberAndClampsItToItsRange()
        {
            DecalView view = Tank();
            Slider hp = view.Slider("slMyHP");

            List<double> raised = new List<double>();
            hp.Changed += (_, e) => raised.Add(e.Position);

            CultureInfo original = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");
                Assert.True(view.Apply(Set("slMyHP", "42.5")));
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }

            Assert.Equal(42.5, hp.Position);

            Assert.True(view.Apply(Set("slMyHP", "150")));
            Assert.Equal(90, hp.Position);

            Assert.True(view.Apply(Set("slMyHP", "-3")));
            Assert.Equal(10, hp.Position);

            Assert.Equal(new[] { 42.5, 90, 10 }, raised);

            Assert.False(view.Apply(Set("slMyHP", "a lot")));
            Assert.False(view.Apply(Set("slMyHP", "NaN")));
            Assert.Equal(3, raised.Count);
        }

        [Fact]
        public void ButtonsOfBothKindsAreClickedByAPress()
        {
            DecalView view = Tank();

            List<string> clicked = new List<string>();
            view.PushButton("bForceBuff").Clicked += (_, e) => clicked.Add(e.Name);
            view.ImageButton("btnNavDown").Clicked += (_, e) => clicked.Add(e.Name);

            Assert.True(view.Apply(new OverlayCommand(ViewVerbs.Press, controlId: "bForceBuff")));
            Assert.True(view.Apply(new OverlayCommand(ViewVerbs.Press, controlId: "btnNavDown")));

            Assert.Equal(new[] { "bForceBuff", "btnNavDown" }, clicked);
        }

        [Fact]
        public void ANotebookTurnsToThePageChosen()
        {
            DecalView view = Tank();
            Notebook tabs = view.Notebook("MainTabs");

            PageChangedEventArgs args = null;
            int seen = -1;
            tabs.PageChanged += (_, e) => { args = e; seen = tabs.ActivePage; };

            Assert.True(view.Apply(new OverlayCommand(ViewVerbs.Page, "2", controlId: "MainTabs")));
            Assert.Equal(2, tabs.ActivePage);
            Assert.Equal(2, seen);
            Assert.Equal(2, args.Page);
            Assert.Equal("Vitals", args.Label);

            args = null;
            Assert.False(view.Apply(new OverlayCommand(ViewVerbs.Page, "3", controlId: "MainTabs")));
            Assert.False(view.Apply(new OverlayCommand(ViewVerbs.Page, "Vitals", controlId: "MainTabs")));
            Assert.Null(args);
            Assert.Equal(2, tabs.ActivePage);
        }

        [Fact]
        public void AClickOnACheckColumnTogglesTheCellBeforeTheHandlerRuns()
        {
            DecalView view = Tank();
            List monsters = view.List("lstMonsters");

            monsters.Add()[1].Text = "Drudge";
            ListRow second = monsters.Add();
            second[1].Text = "Mosswart";

            List<(int row, int column, bool checkedThen)> raised = new List<(int, int, bool)>();
            monsters.Clicked += (_, e) => raised.Add((e.Row, e.Column, monsters[e.Row][0].Checked));

            OverlayCommand onCheck = new OverlayCommand(ViewVerbs.Click, "0", rowId: "1", controlId: "lstMonsters");

            Assert.True(view.Apply(onCheck));
            Assert.True(second[0].Checked);
            Assert.True(view.Apply(onCheck));
            Assert.False(second[0].Checked);

            // A text column's click changes nothing; the plugin decides what it means.
            Assert.True(view.Apply(new OverlayCommand(ViewVerbs.Click, "1", rowId: "0", controlId: "lstMonsters")));
            Assert.False(monsters[0][0].Checked);

            Assert.Equal(new[] { (1, 0, true), (1, 0, false), (0, 1, false) }, raised);
        }

        [Fact]
        public void AClickOutsideTheListIsRefused()
        {
            DecalView view = Tank();
            List monsters = view.List("lstMonsters");
            monsters.Add();

            int raised = 0;
            monsters.Clicked += (_, _) => raised++;

            Assert.False(view.Apply(new OverlayCommand(ViewVerbs.Click, "0", rowId: "1", controlId: "lstMonsters")));
            Assert.False(view.Apply(new OverlayCommand(ViewVerbs.Click, "4", rowId: "0", controlId: "lstMonsters")));
            Assert.False(view.Apply(new OverlayCommand(ViewVerbs.Click, "0", rowId: "0x8000", controlId: "lstMonsters")));
            Assert.False(view.Apply(new OverlayCommand(ViewVerbs.Click, string.Empty, rowId: "0", controlId: "lstMonsters")));

            Assert.Equal(0, raised);
            Assert.False(monsters[0][0].Checked);
        }

        [Fact]
        public void TheWrongVerbForAControlIsRefused()
        {
            DecalView view = Tank();

            int raised = 0;
            view.Checkbox("cExact").Changed += (_, _) => raised++;
            view.PushButton("bForceBuff").Clicked += (_, _) => raised++;

            Assert.False(view.Apply(new OverlayCommand(ViewVerbs.Press, controlId: "cExact")));
            Assert.False(view.Apply(Set("bForceBuff", "true")));
            Assert.False(view.Apply(new OverlayCommand(ViewVerbs.Click, "0", "0", controlId: "cmbNavType")));
            Assert.False(view.Apply(Set("MainTabs", "1")));
            Assert.False(view.Apply(Set("prgBuffs", "10")));
            Assert.False(view.Apply(Set("Label12", "text")));

            Assert.Equal(0, raised);
            Assert.True(view.Checkbox("cExact").Checked);
        }

        [Fact]
        public void AControlThatIsDisabledHiddenOrAbsentTakesNothing()
        {
            DecalView view = Tank();
            Checkbox box = view.Checkbox("cEnableBuffing");

            int raised = 0;
            box.Changed += (_, _) => raised++;

            box.Enabled = false;
            Assert.False(view.Apply(Set("cEnableBuffing", "true")));

            box.Enabled = true;
            box.Visible = false;
            Assert.False(view.Apply(Set("cEnableBuffing", "true")));

            Assert.False(box.Checked);
            Assert.Equal(0, raised);

            Assert.False(view.Apply(Set("cNoSuchThing", "true")));
            Assert.False(view.Apply(Set(string.Empty, "true")));
            Assert.False(view.Apply(null));
        }

        /// <summary>
        /// A handler's exception is the plugin's fault, and the host counts it as one; the
        /// view must not swallow it. The change has already happened by then, which is the
        /// price of the handler seeing the new value.
        /// </summary>
        [Fact]
        public void AnExceptionFromAHandlerReachesTheCaller()
        {
            DecalView view = Tank();
            Checkbox box = view.Checkbox("cEnableBuffing");
            box.Changed += (_, _) => throw new InvalidOperationException("handler broke");

            InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(() => view.Apply(Set("cEnableBuffing", "true")));
            Assert.Equal("handler broke", thrown.Message);
            Assert.True(box.Checked);
        }

        [Fact]
        public void AChoiceAndANotebookRefuseAnIndexWithNothingBehindIt()
        {
            DecalView view = Tank();
            Choice nav = view.Choice("cmbNavType");

            Assert.Throws<ArgumentOutOfRangeException>(() => nav.Selected = 2);
            nav.Selected = -1;
            Assert.Equal(string.Empty, nav.SelectedText);

            nav.Add("Follow");
            nav.Selected = 2;
            Assert.Equal("Follow", nav.SelectedText);

            nav.Clear();
            Assert.Equal(-1, nav.Selected);
            Assert.Equal(0, nav.Count);

            Notebook tabs = view.Notebook("MainTabs");
            Assert.Throws<ArgumentOutOfRangeException>(() => tabs.ActivePage = 3);
            Assert.Throws<ArgumentOutOfRangeException>(() => tabs.ActivePage = -1);
            tabs.ActivePage = 1;
            Assert.Equal(1, tabs.ActivePage);
        }

        [Fact]
        public void EveryListRowHasACellForEveryColumn()
        {
            List monsters = Tank().List("lstMonsters");

            ListRow first = monsters.Add();
            ListRow top = monsters.Insert(0);
            Assert.Equal(4, first.Count);
            Assert.Same(top, monsters[0]);
            Assert.Same(first, monsters[1]);

            first[3].Text = null;
            Assert.Equal(string.Empty, first[3].Text);

            Assert.True(monsters.Remove(top));
            Assert.Same(first, monsters[0]);

            monsters.RemoveAt(0);
            Assert.Equal(0, monsters.RowCount);

            monsters.Add();
            monsters.Add();
            monsters.Clear();
            Assert.Empty(monsters.Rows);
        }
    }
}
