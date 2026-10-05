using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AC.Dat;
using Xunit;

namespace AC.Dat.Tests
{
    /// <summary>
    /// Reading Virindi View Service's store without SQLite. The fixture was written by SQLite
    /// itself (Python's sqlite3), with the table VVS creates and alters, so these check the
    /// reader against the real writer rather than against a reader's idea of one.
    /// </summary>
    public class VirindiViewStoreTests
    {
        private static readonly string Fixture = Path.Combine(AppContext.BaseDirectory, "Data", "views.s3db");

        [Fact]
        public void EveryRowOfATableIsReadInOrderWithEachKindOfValue()
        {
            SqliteFile file = SqliteFile.Open(Fixture);
            Assert.Contains("numbers", file.TableNames());

            IReadOnlyList<IReadOnlyDictionary<string, object>> rows = file.ReadTable("numbers");

            // Three hundred rows on 1 KB pages: the table is a tree of several levels.
            Assert.Equal(300, rows.Count);
            Assert.Equal(Enumerable.Range(1, 300).Select(i => (long)i), rows.Select(r => (long)r["id"]));
            Assert.Equal("row 7", rows[6]["name"]);
            Assert.Equal(-7L * 100000000000, rows[6]["big"]);
            Assert.Equal(8L * 100000000000, rows[7]["big"]);
            Assert.Equal(1.75, rows[6]["ratio"]);
            Assert.Null(rows[6]["note"]);

            // A value longer than a page, carried on overflow pages.
            Assert.Equal(new string('x', 3000), rows[149]["note"]);
        }

        [Fact]
        public void AColumnAddedAfterARowWasWrittenIsMissingFromThatRow()
        {
            IReadOnlyDictionary<string, object> old = SqliteFile.Open(Fixture).ReadTable("StoredViewInfo")
                .Single(r => (string)r["ViewKey"] == "Old:Old Window");

            Assert.Equal(10L, old["LocX"]);
            Assert.False(old.ContainsKey("ClickThrough"));
            Assert.False(old.ContainsKey("ThemeID2"));
        }

        [Fact]
        public void TheStoreGivesEachWindowItsThemeOnlyWhereThePlayerChoseOne()
        {
            Assert.True(VirindiViewStore.TryRead(Fixture, out IReadOnlyDictionary<string, VirindiStoredView> views, out string error), error);

            // Virindi Tank's window: the player picked Decal.
            Assert.Equal("Decal", views["uTank2:uTank2"].Theme);
            Assert.False(views["uTank2:uTank2"].Ghosted);

            // The HUDs: Float is only what the default was when they were saved, so no theme of
            // their own; hudified, and one click-through, where they were left.
            VirindiStoredView remote = views["VirindiHUDs:VT MiniRemote"];
            Assert.Null(remote.Theme);
            Assert.True(remote.Ghosted);
            Assert.True(remote.ClickThrough);
            Assert.Equal((230, 41, 78, 96), (remote.X, remote.Y, remote.Width, remote.Height));
            Assert.Equal(-87, views["VirindiHUDs:Comps HUD"].X);

            // Picked, but stored by number alone: VVS's third theme.
            Assert.Equal("Minimalist Transparent", views["Scrolls:Scrolls v1.0"].Theme);

            // A row from before the theme columns: nothing chosen, nothing click-through.
            Assert.Null(views["Old:Old Window"].Theme);
            Assert.True(views["Old:Old Window"].Ghosted);
            Assert.False(views["Old:Old Window"].ClickThrough);
        }

        /// <summary>
        /// The edges a hudified window was left stuck to, read as VVS read its LocSticky - the left
        /// before the right, the top before the bottom - and the ExtraInfo row its bar kept its
        /// orientation in. Written by SQLite, with VVS's own tables.
        /// </summary>
        [Fact]
        public void TheStoreGivesTheEdgesAWindowWasStuckToAndTheBarsExtraInfo()
        {
            string fixture = Path.Combine(AppContext.BaseDirectory, "Data", "vvsbar.s3db");
            Assert.True(VirindiViewStore.TryRead(fixture, out IReadOnlyDictionary<string, VirindiStoredView> views, out string error), error);

            VirindiStoredView bar = views[VirindiViewStore.BarKey];
            Assert.Equal((0, 300, true, "L"), (bar.X, bar.Y, bar.Ghosted, bar.StuckEdges));
            Assert.Equal("RB", views["VirindiHUDs:StatusHUD"].StuckEdges);
            Assert.Equal("LT", views["VirindiHUDs:Comps HUD"].StuckEdges);
            Assert.Equal((string.Empty, 856, 360), (views["uTank2:uTank2"].StuckEdges, views["uTank2:uTank2"].Width, views["uTank2:uTank2"].Height));

            Assert.True(VirindiViewStore.TryReadExtraInfo(fixture, out IReadOnlyDictionary<string, long> extra, out error), error);
            Assert.Equal(1L, extra["VVSBarHorizontal"]);

            // A store VVS's bar never wrote to has no ExtraInfo table: nothing, and no fault.
            Assert.True(VirindiViewStore.TryReadExtraInfo(Fixture, out extra, out error), error);
            Assert.Empty(extra);
        }

        [Theory]
        [InlineData(null, null, "Float")]
        [InlineData("", null, "Float")]
        [InlineData("Decal", null, "Decal")]
        [InlineData("decal", 0, "Decal")]
        [InlineData(null, 3, "Decal")]
        [InlineData(null, 0, "Minimalist")]
        [InlineData(null, 99, "Float")]
        [InlineData("Some XML Theme", null, "Float")]
        public void VvssDefaultThemeIsFloatUnlessTheRegistrySaysOtherwise(string theme2, int? index, string expected)
        {
            Assert.Equal(expected, VirindiViewStore.DefaultTheme(theme2, index));
        }

        [Fact]
        public void AFileThatIsNotADatabaseIsRefusedWithAReason()
        {
            string path = Path.Combine(Path.GetTempPath(), "not-a-db-" + Guid.NewGuid().ToString("N") + ".s3db");
            File.WriteAllText(path, "hello, this is not SQLite at all, just some words to fill a hundred bytes and more of header space.");
            try
            {
                Assert.False(VirindiViewStore.TryRead(path, out _, out string error));
                Assert.Contains("SQLite", error);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
