using System;
using System.Collections.Generic;
using System.Numerics;

namespace AC.Dat.Geometry
{
    /// <summary>An indoor cell, ready to test: its room's shape, where it stands, its doorways and its furniture.</summary>
    public sealed class IndoorCell
    {
        internal IndoorCell(EnvCellInfo info, RoomShape room, (Plane Plane, uint Other)[] doorways, Obstacle[] statics)
        {
            Info = info;
            Room = room;
            Doorways = doorways;
            Statics = statics;
        }

        public uint Id => Info.Id;

        public EnvCellInfo Info { get; }

        public RoomShape Room { get; }

        public Placement Placement => Info.Placement;

        public bool SeenOutside => Info.SeenOutside;

        /// <summary>Each doorway's plane, in the cell's frame, and the cell beyond it.</summary>
        internal (Plane Plane, uint Other)[] Doorways { get; }

        /// <summary>The cell's furniture, in its landblock's frame.</summary>
        public IReadOnlyList<Obstacle> Statics { get; }

        /// <summary>Whether a point of the landblock is in the cell.</summary>
        public bool Contains(Vector3 point) => Room.Space.Contains(Placement.ToInner(point));

        public override string ToString() => $"0x{Id:X8}";
    }

    /// <summary>
    /// One landblock, ready to test: its ground's triangles and water, the buildings, static
    /// objects and scenery standing on it, and its indoor cells.
    /// </summary>
    public sealed class LandblockGeometry
    {
        /// <summary>Terrain types whose ground is under water: the client's SurfChar table.</summary>
        private const int FirstWaterType = 16, LastWaterType = 20;

        private readonly float[] _heights = new float[81];
        private readonly bool[] _splitSouthWestToNorthEast = new bool[64];
        private readonly byte[] _water = new byte[64];
        private readonly List<Obstacle>[] _cells = new List<Obstacle>[64];
        private readonly ushort[] _terrain;

        internal LandblockGeometry(uint landblock, LandblockGround ground, RegionInfo region)
        {
            Id = landblock & 0xFFFF0000u;
            _terrain = new ushort[81];
            for (int i = 0; i < 81; i++)
            {
                _terrain[i] = ground?.Terrain[i] ?? 0;
                byte height = ground?.Heights[i] ?? 0;
                _heights[i] = region != null && height < region.LandHeights.Count ? region.LandHeights[height] : height * 2f;
            }

            uint gx0 = (Id >> 24) * 8, gy0 = ((Id >> 16) & 0xFF) * 8;
            for (int x = 0; x < 8; x++)
            {
                for (int y = 0; y < 8; y++)
                {
                    _splitSouthWestToNorthEast[(x * 8) + y] = SplitsSouthWestToNorthEast(gx0 + (uint)x, gy0 + (uint)y);
                    int wet = 0;
                    for (int i = x; i <= x + 1; i++)
                        for (int j = y; j <= y + 1; j++)
                            wet += IsWater(_terrain[(i * 9) + j]) ? 1 : 0;
                    _water[(x * 8) + y] = (byte)(wet == 0 ? 0 : wet == 4 ? 2 : 1);
                }
            }

            for (int i = 0; i < 64; i++)
                _cells[i] = new List<Obstacle>();
        }

        /// <summary>The landblock, in the high 16 bits.</summary>
        public uint Id { get; }

        public IReadOnlyList<ushort> Terrain => _terrain;

        /// <summary>Indoor cells, by whole id.</summary>
        public IReadOnlyDictionary<uint, IndoorCell> Cells => _indoor;

        internal readonly Dictionary<uint, IndoorCell> _indoor = new Dictionary<uint, IndoorCell>();

        /// <summary>Everything standing outdoors: buildings, the client's objects, scenery.</summary>
        public IReadOnlyList<Obstacle> Outdoors => _outdoors;

        private readonly List<Obstacle> _outdoors = new List<Obstacle>();

        /// <summary>How far past the landblock's edges any of its outdoor obstacles reach.</summary>
        public float Overhang { get; private set; }

        /// <summary>
        /// Whether a landcell's diagonal runs south-west to north-east, by the client's hash of the
        /// cell's place in the world (its LandblockStruct's ConstructPolygons).
        /// </summary>
        internal static bool SplitsSouthWestToNorthEast(uint gx, uint gy)
        {
            uint hash = unchecked((gy * ((gx * 214614067u) + 1813693831u)) - (gx * 1109124029u) - 1369149221u);
            return hash >= 0x80000000u;
        }

        private static bool IsWater(ushort terrain)
        {
            int type = (terrain >> 2) & 0x1F;
            return type >= FirstWaterType && type <= LastWaterType;
        }

        /// <summary>The height at a vertex, by its east and north indices.</summary>
        public float VertexHeight(int x, int y) => _heights[(x * 9) + y];

        internal void Add(Obstacle obstacle)
        {
            _outdoors.Add(obstacle);
            float r = obstacle.BoundsRadius;
            Vector3 c = obstacle.BoundsCentre;
            if (float.IsInfinity(r))
            {
                // No sphere round it: it may be anywhere, so every landcell looks at it.
                foreach (List<Obstacle> cell in _cells)
                    cell.Add(obstacle);
                return;
            }

            Overhang = Math.Max(Overhang, Math.Max(Math.Max(r - c.X, r - c.Y), Math.Max(c.X + r - 192f, c.Y + r - 192f)));
            int x0 = Math.Clamp((int)Math.Floor((c.X - r) / 24f), 0, 7), x1 = Math.Clamp((int)Math.Floor((c.X + r) / 24f), 0, 7);
            int y0 = Math.Clamp((int)Math.Floor((c.Y - r) / 24f), 0, 7), y1 = Math.Clamp((int)Math.Floor((c.Y + r) / 24f), 0, 7);
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                    _cells[(x * 8) + y].Add(obstacle);
        }

        /// <summary>
        /// The outdoor obstacles that a sphere about <paramref name="centre"/> of
        /// <paramref name="radius"/> may reach, by the landcells it spans: one landcell's own list
        /// when it spans one, as it nearly always does, else those of all it spans gathered into
        /// <paramref name="scratch"/>.
        /// </summary>
        internal IReadOnlyList<Obstacle> Near(Vector3 centre, float radius, List<Obstacle> scratch)
        {
            int x0 = (int)Math.Floor((centre.X - radius) / 24f), x1 = (int)Math.Floor((centre.X + radius) / 24f);
            int y0 = (int)Math.Floor((centre.Y - radius) / 24f), y1 = (int)Math.Floor((centre.Y + radius) / 24f);
            if (x1 < 0 || y1 < 0 || x0 > 7 || y0 > 7)
                return Array.Empty<Obstacle>();

            x0 = Math.Max(x0, 0);
            y0 = Math.Max(y0, 0);
            x1 = Math.Min(x1, 7);
            y1 = Math.Min(y1, 7);
            if (x0 == x1 && y0 == y1)
                return _cells[(x0 * 8) + y0];

            scratch.Clear();
            for (int x = x0; x <= x1; x++)
            {
                for (int y = y0; y <= y1; y++)
                {
                    foreach (Obstacle obstacle in _cells[(x * 8) + y])
                    {
                        if (!scratch.Contains(obstacle))
                            scratch.Add(obstacle);
                    }
                }
            }

            return scratch;
        }

        /// <summary>
        /// The ground's triangle under a point of the landblock, as the plane through it facing up;
        /// false outside the landblock.
        /// </summary>
        public bool TryGetGround(Vector3 point, out Plane plane)
        {
            plane = default;
            if (point.X < 0 || point.Y < 0 || point.X > 192 || point.Y > 192)
                return false;

            int x = Math.Min((int)(point.X / 24f), 7);
            int y = Math.Min((int)(point.Y / 24f), 7);
            Vector3 sw = Corner(x, y), se = Corner(x + 1, y), nw = Corner(x, y + 1), ne = Corner(x + 1, y + 1);

            (Vector3 A, Vector3 B, Vector3 C) first, second;
            if (_splitSouthWestToNorthEast[(x * 8) + y])
            {
                first = (sw, se, ne);
                second = (sw, ne, nw);
            }
            else
            {
                first = (sw, se, nw);
                second = (ne, nw, se);
            }

            (Vector3 A, Vector3 B, Vector3 C) under = Inside2D(first, point) ? first : Inside2D(second, point) ? second : default;
            if (under == default)
                return false;

            Vector3 normal = Vector3.Normalize(Vector3.Cross(under.B - under.A, under.C - under.A));
            if (normal.Z < 0)
                normal = -normal;
            plane = new Plane(normal, -Vector3.Dot(normal, under.A));
            return true;
        }

        /// <summary>The ground's height under a point of the landblock, or null outside it.</summary>
        public float? GroundHeight(Vector3 point)
        {
            if (!TryGetGround(point, out Plane plane) || Math.Abs(plane.Normal.Z) <= 0.0002f)
                return null;
            return -((plane.Normal.X * point.X) + (plane.Normal.Y * point.Y) + plane.D) / plane.Normal.Z;
        }

        /// <summary>
        /// How far below its surface the ground under a point is water: the client's water depth -
        /// 0.9 for a landcell all water, 0.45 or 0.1 in a part-water one by the nearest vertex, 0
        /// for dry land.
        /// </summary>
        public float WaterDepth(Vector3 point)
        {
            int x = Math.Clamp((int)(point.X / 24f), 0, 7);
            int y = Math.Clamp((int)(point.Y / 24f), 0, 7);
            switch (_water[(x * 8) + y])
            {
                case 0:
                    return 0f;
                case 2:
                    return 0.9f;
            }

            int vertex = (x * 9) + y;
            if (point.X % 24f >= 12f)
                vertex += 9;
            if (point.Y % 24f >= 12f)
                vertex += 1;
            return IsWater(_terrain[vertex]) ? 0.45f : 0.1f;
        }

        private Vector3 Corner(int x, int y) => new Vector3(x * 24f, y * 24f, _heights[(x * 9) + y]);

        private static bool Inside2D((Vector3 A, Vector3 B, Vector3 C) t, Vector3 p)
        {
            float d1 = Side(p, t.A, t.B), d2 = Side(p, t.B, t.C), d3 = Side(p, t.C, t.A);
            bool negative = d1 < -0.0002f || d2 < -0.0002f || d3 < -0.0002f;
            bool positive = d1 > 0.0002f || d2 > 0.0002f || d3 > 0.0002f;
            return !(negative && positive);
        }

        private static float Side(Vector3 p, Vector3 a, Vector3 b) => ((p.X - b.X) * (a.Y - b.Y)) - ((a.X - b.X) * (p.Y - b.Y));
    }
}
