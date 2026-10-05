using System;
using System.Linq;
using AC.Dat;
using Xunit;

namespace AC.Dat.Tests
{
    /// <summary>
    /// The client's cell archive, read for what can be seen from its indoor cells, and the archive
    /// read without an index. Like the portal tests these read the real files, and skip on a
    /// machine without them; one cell's bytes are also kept here, so its layout is checked anywhere.
    /// </summary>
    public class CellDataTests
    {
        private static readonly string PortalPath = PortalData.FindPortalDat();

        private static readonly string CellPath = CellData.FindBeside(PortalPath);

        /// <summary>
        /// The room where the Grand Master Scrivener of War Magic stands in the recorded sessions,
        /// 0x2B12012E, as the archive holds it: it sees outside, and sees 13 other cells of its
        /// landblock.
        /// </summary>
        private const string ScrivenersRoomHex =
            "2E01122B010000002E01122B09020D00C9011D071B071A07BB041C071A0719071F07AD03000000000443000070420000404231BD3BB30000000000000000000080BF"
            + "010017003A01000001001600370101002F0130013101320133013401350136013701380139013A013B01";

        [Fact]
        public void AnIndoorCellReadsAsWhatCanBeSeenFromIt()
        {
            EnvCellInfo room = EnvCellInfo.Parse(Convert.FromHexString(ScrivenersRoomHex));

            Assert.Equal(0x2B12012Eu, room.Id);
            Assert.True(room.SeenOutside);
            Assert.Equal(13, room.VisibleCells.Count);
            Assert.True(room.Sees(0x0131));
            Assert.True(room.Sees(0x013B));
            Assert.False(room.Sees(0x012E));
            Assert.False(room.Sees(0x0130 + 0x20));
        }

        [Fact]
        public void BytesTooShortForTheirCountsAreNoCell()
        {
            byte[] cut = Convert.FromHexString(ScrivenersRoomHex).Take(100).ToArray();

            Assert.Null(EnvCellInfo.Parse(cut));
            Assert.Null(EnvCellInfo.Parse(new byte[8]));
            Assert.Null(EnvCellInfo.Parse(null));
        }

        [SkippableFact]
        public void WithoutAnIndexTheArchiveFindsWhatTheIndexFinds()
        {
            Skip.If(PortalPath == null, "No client_portal.dat on this machine.");

            using DatDatabase indexed = DatDatabase.Open(PortalPath);
            using DatDatabase searched = DatDatabase.Open(PortalPath, indexed: false);

            Assert.False(searched.IsIndexed);
            Assert.Equal(0, searched.FileCount);

            uint[] ids = indexed.Files.Keys.OrderBy(id => id).ToArray();
            for (int i = 0; i < ids.Length; i += 97)
                Assert.Equal(indexed.Read(ids[i]), searched.Read(ids[i]));

            Assert.True(searched.Contains(ids[0]));
            Assert.True(searched.Contains(ids[ids.Length - 1]));
            Assert.True(searched.Contains(SpellTable.FileId));
            Assert.Null(searched.Read(0xDEADBEEF));
            Assert.False(searched.Contains(0xDEADBEEF));
        }

        [SkippableFact]
        public void TheCellArchiveSaysWhichCellsSeeOutside()
        {
            Skip.If(CellPath == null, "No client_cell_1.dat beside client_portal.dat on this machine.");

            using CellData cells = CellData.Open(CellPath);

            EnvCellInfo room = cells.GetEnvCell(0x2B12012E);
            Assert.NotNull(room);
            Assert.True(room.SeenOutside);
            Assert.True(room.Sees(0x0131));

            // A dungeon's cell: no outside, and the cells beyond its doors.
            EnvCellInfo dungeon = cells.GetEnvCell(0x01D90100);
            Assert.NotNull(dungeon);
            Assert.False(dungeon.SeenOutside);
            Assert.True(dungeon.Sees(0x0101));

            // Outdoor cells and the landblock's own files are not indoor cells.
            Assert.Null(cells.GetEnvCell(0x2B110028));
            Assert.Null(cells.GetEnvCell(0x2B12FFFE));
            Assert.Null(cells.GetEnvCell(0x2B12FFFF));
        }
    }
}
