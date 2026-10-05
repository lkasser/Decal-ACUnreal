using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;

namespace AC.Dat
{
    /// <summary>
    /// One indoor cell - a room of a building, a stretch of a dungeon - as far as seeing out
    /// of it goes: whether outside is in view from it, and which other cells are.
    /// </summary>
    /// <remarks>
    /// The game decides what a character can see by cells. Outdoors that is a matter of
    /// landblocks, but indoors each cell lists the cells of its landblock that can be seen
    /// from it, and says whether it can see outside at all. ACE reads the same two things from
    /// the same file to decide which objects the client is told of (its EnvCell's VisibleCells
    /// and SeenOutside).
    /// </remarks>
    public sealed class EnvCellInfo
    {
        /// <summary>The cell flag saying outside can be seen from it: EnvCellFlags.SeenOutside.</summary>
        private const uint SeenOutsideFlag = 0x1;

        /// <summary>The cell flag saying static objects follow the visible cells.</summary>
        private const uint HasStaticsFlag = 0x2;

        private readonly HashSet<ushort> _visible;

        public EnvCellInfo(uint id, bool seenOutside, IEnumerable<ushort> visibleCells)
        {
            Id = id;
            SeenOutside = seenOutside;
            _visible = visibleCells != null ? new HashSet<ushort>(visibleCells) : new HashSet<ushort>();
        }

        /// <summary>The Environment (file 0x0D......) whose room shapes the cell is built of.</summary>
        public uint EnvironmentId { get; private set; }

        /// <summary>Which of the Environment's room shapes the cell is.</summary>
        public ushort RoomIndex { get; private set; }

        /// <summary>Where the room stands in its landblock.</summary>
        public Geometry.Placement Placement { get; private set; } = Geometry.Placement.Identity;

        /// <summary>The cell's doorways, to the cells beyond them.</summary>
        public IReadOnlyList<CellDoorway> Doorways { get; private set; } = Array.Empty<CellDoorway>();

        /// <summary>The furniture and fittings the cell holds, each a model and where it stands in the landblock.</summary>
        public IReadOnlyList<StaticObject> StaticObjects { get; private set; } = Array.Empty<StaticObject>();

        /// <summary>The cell: its landblock in the high 16 bits, the cell (0x0100 and up) in the low.</summary>
        public uint Id { get; }

        /// <summary>Whether outside can be seen from the cell: a building's room can, a dungeon cannot.</summary>
        public bool SeenOutside { get; }

        /// <summary>The other cells of the landblock in view from this one, by their low 16 bits.</summary>
        public IReadOnlyCollection<ushort> VisibleCells => _visible;

        /// <summary>Whether a cell of the same landblock, given by its low 16 bits, is in view from this one.</summary>
        public bool Sees(ushort cell) => _visible.Contains(cell);

        /// <summary>
        /// Reads an indoor cell's file. Its layout, as ACE's DatLoader reads an EnvCell: the id,
        /// the flags, the id again, the counts of surfaces (a byte), portals (a byte) and visible
        /// cells (a half-word), the surfaces, the environment and the cell structure (half-words
        /// each), the cell's frame (seven floats), the portals (four half-words each: flags, the
        /// doorway's polygon, the cell beyond and its doorway), the visible cells as half-words,
        /// then - where the flags say - the static objects (a count, then each its model and
        /// frame). The restriction after them is not needed here.
        /// </summary>
        /// <returns>The cell, or null when the bytes do not hold one.</returns>
        public static EnvCellInfo Parse(byte[] data)
        {
            if (data == null || data.Length < 16)
                return null;

            uint id = BitConverter.ToUInt32(data, 0);
            uint flags = BitConverter.ToUInt32(data, 4);
            int surfaces = data[12];
            int portals = data[13];
            int visible = BitConverter.ToUInt16(data, 14);

            long shape = 16L + (surfaces * 2L);
            long cursor = shape + 2 + 2 + (7 * 4) + (portals * 8L);
            if (cursor + (visible * 2L) > data.Length)
                return null;

            List<ushort> cells = new List<ushort>(visible);
            for (int i = 0; i < visible; i++)
                cells.Add(BitConverter.ToUInt16(data, (int)cursor + (i * 2)));

            EnvCellInfo cell = new EnvCellInfo(id, (flags & SeenOutsideFlag) != 0, cells)
            {
                EnvironmentId = 0x0D000000u | BitConverter.ToUInt16(data, (int)shape),
                RoomIndex = BitConverter.ToUInt16(data, (int)shape + 2),
                Placement = ReadPlacement(data, (int)shape + 4),
            };

            uint landblock = id & 0xFFFF0000u;
            CellDoorway[] doorways = new CellDoorway[portals];
            for (int i = 0; i < portals; i++)
            {
                int at = (int)shape + 4 + 28 + (i * 8);
                doorways[i] = new CellDoorway(BitConverter.ToUInt16(data, at + 2), landblock | BitConverter.ToUInt16(data, at + 4));
            }

            cell.Doorways = doorways;

            cursor += visible * 2L;
            if ((flags & HasStaticsFlag) != 0)
            {
                if (cursor + 4 > data.Length)
                    return null;

                uint count = BitConverter.ToUInt32(data, (int)cursor);
                cursor += 4;
                if (cursor + (count * 32L) > data.Length)
                    return null;

                StaticObject[] statics = new StaticObject[count];
                for (int i = 0; i < count; i++)
                {
                    int at = (int)cursor + (i * 32);
                    statics[i] = new StaticObject(BitConverter.ToUInt32(data, at), ReadPlacement(data, at + 4));
                }

                cell.StaticObjects = statics;
            }

            return cell;
        }

        /// <summary>Seven floats: the origin, then the rotation stored w first.</summary>
        internal static Geometry.Placement ReadPlacement(byte[] data, int at)
        {
            float F(int i) => BitConverter.ToSingle(data, at + (i * 4));
            return new Geometry.Placement(new System.Numerics.Vector3(F(0), F(1), F(2)), new System.Numerics.Quaternion(F(4), F(5), F(6), F(3)));
        }

        public override string ToString() => $"0x{Id:X8}{(SeenOutside ? " (sees outside)" : string.Empty)}, {_visible.Count} cells in view";
    }

    /// <summary>A doorway out of an indoor cell: the room polygon it is cut in, and the cell beyond.</summary>
    public readonly struct CellDoorway
    {
        public CellDoorway(ushort polygonId, uint otherCellId)
        {
            PolygonId = polygonId;
            OtherCellId = otherCellId;
        }

        public ushort PolygonId { get; }

        /// <summary>The cell beyond, whole: its landblock and its number; a number of 0xFFFF is outside.</summary>
        public uint OtherCellId { get; }

        public override string ToString() => $"polygon {PolygonId} to 0x{OtherCellId:X8}";
    }

    /// <summary>
    /// An object the client places itself - a building's furniture, a landblock's fences and rocks:
    /// its model (a Setup 0x02...... or a GfxObj 0x01......) and where it stands in its landblock.
    /// </summary>
    public readonly struct StaticObject
    {
        public StaticObject(uint modelId, Geometry.Placement placement)
        {
            ModelId = modelId;
            Placement = placement;
        }

        public uint ModelId { get; }

        public Geometry.Placement Placement { get; }

        public override string ToString() => $"0x{ModelId:X8} at {Placement.Origin}";
    }

    /// <summary>Indoor cells by id, wherever they come from.</summary>
    public interface ICellData
    {
        /// <summary>The indoor cell, or null when the id is not one or the cell is not known.</summary>
        EnvCellInfo GetEnvCell(uint cellId);
    }

    /// <summary>
    /// The client's cell archive, <c>client_cell_1.dat</c>: the landscape and every indoor
    /// cell. Only the indoor cells are read, for what can be seen from them.
    /// </summary>
    /// <remarks>
    /// Opened without an index, since a session goes into few of its 800,000 files; each cell
    /// is read the first time it is asked for and kept.
    /// </remarks>
    public sealed class CellData : ICellData, IDisposable
    {
        public const string FileName = "client_cell_1.dat";

        private readonly DatDatabase _dat;
        private readonly ConcurrentDictionary<uint, EnvCellInfo> _cells = new ConcurrentDictionary<uint, EnvCellInfo>();

        private CellData(DatDatabase dat)
        {
            _dat = dat;
        }

        public string Path => _dat.Path;

        /// <summary>Opens <c>client_cell_1.dat</c>.</summary>
        /// <exception cref="FileNotFoundException">No such file.</exception>
        /// <exception cref="InvalidDataException">Not a DAT.</exception>
        public static CellData Open(string cellDatPath) => new CellData(DatDatabase.Open(cellDatPath, indexed: false));

        /// <summary>The cell archive beside a portal archive, the way the client's folder holds them; null when there is none.</summary>
        public static string FindBeside(string portalDatPath)
        {
            string directory = string.IsNullOrEmpty(portalDatPath) ? null : System.IO.Path.GetDirectoryName(portalDatPath);
            if (string.IsNullOrEmpty(directory))
                return null;

            string path = System.IO.Path.Combine(directory, FileName);
            return File.Exists(path) ? path : null;
        }

        public EnvCellInfo GetEnvCell(uint cellId)
        {
            uint cell = cellId & 0xFFFF;
            if (cell < 0x0100 || cell >= 0xFFFE)
                return null;

            return _cells.GetOrAdd(cellId, id => EnvCellInfo.Parse(_dat.Read(id)));
        }

        /// <summary>One file of the archive whole - a landblock's ground or layout, a cell - or null for none.</summary>
        public byte[] ReadFile(uint fileId) => _dat.Read(fileId);

        public void Dispose() => _dat.Dispose();
    }
}
