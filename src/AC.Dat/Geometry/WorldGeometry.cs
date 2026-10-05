using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

namespace AC.Dat.Geometry
{
    /// <summary>What a sphere sent through the world met first.</summary>
    public enum ContactKind
    {
        /// <summary>Nothing.</summary>
        None,

        /// <summary>The ground: the sphere went under it, or - in flight - came within its radius of it.</summary>
        Ground,

        /// <summary>A wall, floor or ceiling of an indoor cell.</summary>
        Wall,

        /// <summary>A building's shell, or an object the client places itself: furniture, a fence, a tree.</summary>
        Static,

        /// <summary>One of the server's objects the caller asked to be tested.</summary>
        Object,

        /// <summary>A place with no geometry to it: a landblock or cell the archives do not have.</summary>
        Unknown,
    }

    /// <summary>The first thing a sweep met, and after which of its moves.</summary>
    public readonly struct SweepContact
    {
        public SweepContact(int step, ContactKind kind, uint objectId = 0)
        {
            Step = step;
            Kind = kind;
            ObjectId = objectId;
        }

        public static SweepContact Clear => new SweepContact(-1, ContactKind.None);

        /// <summary>The move after which the contact came, from 0; -1 for none.</summary>
        public int Step { get; }

        public ContactKind Kind { get; }

        /// <summary>For an <see cref="ContactKind.Object"/>, the server's id of it.</summary>
        public uint ObjectId { get; }

        public override string ToString() => Kind == ContactKind.None ? "clear" : $"{Kind}{(ObjectId != 0 ? $" 0x{ObjectId:X8}" : string.Empty)} at step {Step}";
    }

    /// <summary>A place in a cell: the cell, whole, and a point in its landblock's frame.</summary>
    public readonly struct CellPoint
    {
        public CellPoint(uint cell, Vector3 point)
        {
            Cell = cell;
            Point = point;
        }

        public uint Cell { get; }

        public Vector3 Point { get; }

        public bool IsIndoors => (Cell & 0xFFFF) >= 0x100;

        /// <summary>The landblock, in the high 16 bits.</summary>
        public uint Landblock => Cell & 0xFFFF0000u;

        /// <summary>The point in the world's flat frame: the landblock's corner plus the point.</summary>
        public Vector3 Flat => new Vector3(((Cell >> 24) * 192f) + Point.X, (((Cell >> 16) & 0xFF) * 192f) + Point.Y, Point.Z);

        public override string ToString() => $"0x{Cell:X8} {Point}";
    }

    /// <summary>
    /// The world's solid geometry, read from the client's archives as Virindi Tank read it from
    /// the client's memory: the ground, the buildings, the static objects and scenery of the
    /// landscape, and the walls and furniture of indoor cells. A sphere can be moved through it,
    /// following doorways from cell to cell, and asked what it touches.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Landblocks are read whole the first time they are asked for and kept: the ground, the
    /// layout, the scenery, every indoor cell with its room shape and furniture, and the shapes
    /// of everything standing there. That takes from one to some tens of milliseconds, so a
    /// caller on a thread that must not wait asks <see cref="IsLoaded"/> first and has
    /// <see cref="Load"/> done elsewhere. Shapes and room shapes are shared between landblocks.
    /// Everything kept is never changed after it is made, so any thread may test against it.
    /// </para>
    /// <para>
    /// The tests are Virindi Tank's (`bm`, `eo`, `b9`, `du`): outdoors, a building's shell, then
    /// the ground, then what stands there, then the building's rooms; indoors, the cell the sphere
    /// is in and those through its doorways, each only where the sphere reaches into it.
    /// </para>
    /// </remarks>
    public sealed class WorldGeometry
    {
        private readonly Func<uint, byte[]> _portal;
        private readonly Func<uint, byte[]> _cells;
        private readonly Lazy<RegionInfo> _region;
        private readonly ConcurrentDictionary<uint, Lazy<LandblockGeometry>> _landblocks = new ConcurrentDictionary<uint, Lazy<LandblockGeometry>>();
        private readonly ConcurrentDictionary<uint, Lazy<ObjectShape>> _shapes = new ConcurrentDictionary<uint, Lazy<ObjectShape>>();
        private readonly ConcurrentDictionary<uint, Lazy<SolidModel>> _models = new ConcurrentDictionary<uint, Lazy<SolidModel>>();
        private readonly ConcurrentDictionary<uint, Lazy<RoomShapes>> _rooms = new ConcurrentDictionary<uint, Lazy<RoomShapes>>();
        private readonly ConcurrentDictionary<uint, Lazy<IReadOnlyList<SceneryObject>>> _scenes = new ConcurrentDictionary<uint, Lazy<IReadOnlyList<SceneryObject>>>();

        /// <summary>How far past its edges any landblock read so far has something standing.</summary>
        private float _overhang;

        [ThreadStatic]
        private static List<Obstacle> _scratch, _besideScratch;

        /// <summary>A list for gathering obstacles, one per thread, so testing allocates nothing.</summary>
        private static List<Obstacle> Scratch => _scratch ??= new List<Obstacle>();

        /// <summary>Another, for the landblocks beside, while the first is still in use.</summary>
        private static List<Obstacle> BesideScratch => _besideScratch ??= new List<Obstacle>();

        /// <param name="portal">Reads a file of the portal archive, or null for none.</param>
        /// <param name="cells">Reads a file of the cell archive, or null for none.</param>
        public WorldGeometry(Func<uint, byte[]> portal, Func<uint, byte[]> cells)
        {
            _portal = portal ?? throw new ArgumentNullException(nameof(portal));
            _cells = cells ?? throw new ArgumentNullException(nameof(cells));
            _region = new Lazy<RegionInfo>(() => RegionInfo.Parse(_portal(RegionInfo.FileId)), LazyThreadSafetyMode.ExecutionAndPublication);
        }

        /// <summary>Over the client's two archives.</summary>
        public static WorldGeometry Open(PortalData portal, CellData cells)
            => new WorldGeometry(portal.ReadFile, cells.ReadFile);

        public RegionInfo Region => _region.Value;

        // ------------------------------------------------------------------- loading

        /// <summary>Whether a landblock (its id in the high 16 bits) has been read.</summary>
        public bool IsLoaded(uint landblock)
            => _landblocks.TryGetValue(landblock & 0xFFFF0000u, out Lazy<LandblockGeometry> lazy) && lazy.IsValueCreated;

        /// <summary>A landblock, read now if it has not been; null when the archive has no ground for it.</summary>
        public LandblockGeometry Load(uint landblock)
        {
            uint id = landblock & 0xFFFF0000u;
            return _landblocks.GetOrAdd(id, key => new Lazy<LandblockGeometry>(() => Read(key), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        }

        /// <summary>A landblock if it has been read, never reading it.</summary>
        public LandblockGeometry Loaded(uint landblock)
            => _landblocks.TryGetValue(landblock & 0xFFFF0000u, out Lazy<LandblockGeometry> lazy) && lazy.IsValueCreated ? lazy.Value : null;

        /// <summary>Whether the shape of a Setup or GfxObj has been read.</summary>
        public bool HasShape(uint modelId) => _shapes.TryGetValue(modelId, out Lazy<ObjectShape> lazy) && lazy.IsValueCreated;

        /// <summary>The shape of a Setup (0x02......) or a bare GfxObj (0x01......), read now if it has not been; null for neither.</summary>
        public ObjectShape Shape(uint modelId)
            => _shapes.GetOrAdd(modelId, id => new Lazy<ObjectShape>(() => ReadShape(id), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

        private ObjectShape ReadShape(uint id)
        {
            switch (id >> 24)
            {
                case 0x01:
                    return ObjectShape.OfModel(Model(id));
                case 0x02:
                    return ObjectShape.Of(ObjectSetup.Parse(_portal(id)), Model);
                default:
                    return null;
            }
        }

        private SolidModel Model(uint id)
            => _models.GetOrAdd(id, key => new Lazy<SolidModel>(() => SolidModel.Parse(_portal(key)), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

        private RoomShapes Rooms(uint id)
            => _rooms.GetOrAdd(id, key => new Lazy<RoomShapes>(() => RoomShapes.Parse(_portal(key)), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

        private IReadOnlyList<SceneryObject> Scene(uint id)
            => _scenes.GetOrAdd(id, key => new Lazy<IReadOnlyList<SceneryObject>>(() => SceneFile.Parse(_portal(key)), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

        /// <summary>
        /// Reads a landblock whole: its ground, its buildings and static objects, its scenery,
        /// and its indoor cells.
        /// </summary>
        private LandblockGeometry Read(uint id)
        {
            LandblockGround ground = LandblockGround.Parse(_cells(id | 0xFFFF));
            if (ground == null)
                return null;

            LandblockGeometry block = new LandblockGeometry(id, ground, Region);
            LandblockLayout layout = LandblockLayout.Parse(_cells(id | 0xFFFE));
            bool[] built = new bool[64];

            if (layout != null)
            {
                foreach (BuildingInfo building in layout.Buildings)
                {
                    if (!InBlock(building.Placement.Origin) || !(Shape(building.ModelId) is ObjectShape shape))
                        continue;

                    built[Landcell(building.Placement.Origin)] = true;
                    block.Add(new Obstacle(shape, building.Placement) { IsBuilding = true, DoorwayCells = building.DoorwayCells });
                }

                foreach (StaticObject placed in layout.Objects)
                {
                    if (!InBlock(placed.Placement.Origin) || !(Shape(placed.ModelId) is ObjectShape shape) || shape.IsEmpty)
                        continue;
                    block.Add(new Obstacle(shape, placed.Placement));
                }

                for (int i = 0; i < layout.CellCount; i++)
                {
                    uint cellId = id | (uint)(0x0100 + i);
                    if (ReadIndoor(cellId) is IndoorCell cell)
                        block._indoor[cellId] = cell;
                }

                foreach (Obstacle building in block.Outdoors)
                {
                    if (!building.IsBuilding)
                        continue;

                    List<IndoorCell> doorways = new List<IndoorCell>(), rooms = new List<IndoorCell>();
                    foreach (uint doorway in building.DoorwayCells)
                    {
                        if (!block._indoor.TryGetValue(doorway, out IndoorCell room))
                            continue;
                        doorways.Add(room);
                        if (!rooms.Contains(room))
                            rooms.Add(room);
                    }

                    foreach (IndoorCell room in doorways)
                    {
                        foreach (ushort seen in room.Info.VisibleCells)
                        {
                            if (block._indoor.TryGetValue(id | seen, out IndoorCell beyond) && !rooms.Contains(beyond))
                                rooms.Add(beyond);
                        }
                    }

                    building.DoorwayRooms = doorways.ToArray();
                    building.Rooms = rooms.ToArray();
                }
            }

            Scenery.Place(block, Region, Scene, Shape, cell => built[cell]);

            float overhang;
            do
            {
                overhang = Volatile.Read(ref _overhang);
            }
            while (block.Overhang > overhang && Interlocked.CompareExchange(ref _overhang, block.Overhang, overhang) != overhang);

            return block;
        }

        private IndoorCell ReadIndoor(uint cellId)
        {
            EnvCellInfo info = EnvCellInfo.Parse(_cells(cellId));
            RoomShape room = info == null ? null : Rooms(info.EnvironmentId)?.Get(info.RoomIndex);
            if (room == null)
                return null;

            // A doorway whose polygon the room does not list is still a way through; with no
            // plane to say which side a point is on, it is always tried.
            List<(Plane, uint)> doorways = new List<(Plane, uint)>();
            foreach (CellDoorway doorway in info.Doorways)
            {
                Plane plane = room.PortalPlanes.TryGetValue(doorway.PolygonId, out Plane found) ? found : new Plane(Vector3.Zero, 0f);
                doorways.Add((plane, doorway.OtherCellId));
            }

            List<Obstacle> statics = new List<Obstacle>();
            foreach (StaticObject placed in info.StaticObjects)
            {
                if (Shape(placed.ModelId) is ObjectShape shape && !shape.IsEmpty)
                    statics.Add(new Obstacle(shape, placed.Placement));
            }

            return new IndoorCell(info, room, doorways.ToArray(), statics.ToArray());
        }

        private static bool InBlock(Vector3 point) => point.X >= 0 && point.Y >= 0 && point.X < 192 && point.Y < 192;

        private static int Landcell(Vector3 point) => (Math.Clamp((int)(point.X / 24f), 0, 7) * 8) + Math.Clamp((int)(point.Y / 24f), 0, 7);

        /// <summary>An indoor cell of a landblock already read; null for none.</summary>
        public IndoorCell Indoor(uint cellId)
        {
            LandblockGeometry block = Loaded(cellId);
            return block != null && block.Cells.TryGetValue(cellId, out IndoorCell cell) ? cell : null;
        }

        // ------------------------------------------------------------------- moving

        /// <summary>
        /// The landcell a point of any cell's landblock lies in, with the point in that landcell's
        /// landblock: the client's own working out of an outdoor position. False off the map.
        /// </summary>
        public static bool ToOutdoors(uint cell, Vector3 point, out CellPoint outdoors)
        {
            outdoors = default;
            if (Math.Abs(point.X) < 0.0002f)
                point.X = 0;
            if (Math.Abs(point.Y) < 0.0002f)
                point.Y = 0;

            long gx = ((cell >> 24) * 8L) + (long)Math.Floor(point.X / 24f);
            long gy = (((cell >> 16) & 0xFF) * 8L) + (long)Math.Floor(point.Y / 24f);
            if (gx < 0 || gy < 0 || gx >= 2040 || gy >= 2040)
                return false;

            uint landblock = (uint)(((gx / 8) << 24) | ((gy / 8) << 16));
            uint landcell = (uint)(((gx % 8) * 8) + (gy % 8) + 1);
            point.X -= (float)Math.Floor(point.X / 192f) * 192f;
            point.Y -= (float)Math.Floor(point.Y / 192f) * 192f;
            outdoors = new CellPoint(landblock | landcell, point);
            return true;
        }

        /// <summary>
        /// Moves a point by <paramref name="offset"/>, following it from cell to cell as Virindi
        /// Tank did (`bm.a(ref int, ref go, gi)`): indoors, into whichever cell through the doorways
        /// it is now in, or out onto the landscape when it is in none; outdoors, across landblocks,
        /// and into a building's room when it is in one.
        /// </summary>
        /// <param name="left">True when the move took the point out of a building or dungeon.</param>
        /// <param name="previous">The cell it was in before the move.</param>
        public CellPoint Move(CellPoint from, Vector3 offset, out bool left, out uint previous)
        {
            left = false;
            previous = from.Cell;
            Vector3 to = from.Point + offset;

            if (from.IsIndoors)
            {
                IndoorCell cell = Indoor(from.Cell);
                if (cell == null)
                    return new CellPoint(from.Cell, to);

                uint found = FindIndoor(cell, from.Point, to, new HashSet<uint>());
                if (found != 0)
                    return new CellPoint(found, to);

                left = true;
                return ToOutdoors(from.Cell, to, out CellPoint outside) ? outside : new CellPoint(from.Cell, to);
            }

            if (!ToOutdoors(from.Cell, to, out CellPoint outdoors))
                return new CellPoint(from.Cell, to);

            LandblockGeometry block = Loaded(outdoors.Cell);
            if (block == null)
                return outdoors;

            IReadOnlyList<Obstacle> standing = block.Near(outdoors.Point, 0f, Scratch);
            for (int i = 0; i < standing.Count; i++)
            {
                Obstacle building = standing[i];
                if (!building.IsBuilding || !SolidTree.SpheresMeet(building.BoundsCentre, building.BoundsRadius, outdoors.Point, 0f))
                    continue;

                foreach (IndoorCell room in building.Rooms)
                {
                    if (room.Contains(outdoors.Point))
                        return new CellPoint(room.Id, outdoors.Point);
                }
            }

            return outdoors;
        }

        /// <summary>
        /// The cell a point now lies in, from the one it was in, through the doorways it is behind
        /// at either end of its move, depth first (`bm.a(b9, ...)`); 0 for none.
        /// </summary>
        private uint FindIndoor(IndoorCell cell, Vector3 from, Vector3 to, HashSet<uint> tried)
        {
            if (cell.Contains(to))
                return cell.Id;

            Vector3 fromInside = cell.Placement.ToInner(from), toInside = cell.Placement.ToInner(to);
            foreach ((Plane plane, uint other) in cell.Doorways)
            {
                if (Plane.DotCoordinate(plane, fromInside) > 0 && Plane.DotCoordinate(plane, toInside) > 0)
                    continue;
                if (!tried.Add(other) || !(Indoor(other) is IndoorCell beyond))
                    continue;

                uint found = FindIndoor(beyond, from, to, tried);
                if (found != 0)
                    return found;
            }

            return 0;
        }

        // ------------------------------------------------------------------- touching

        /// <summary>
        /// What a sphere about a point touches, in Virindi Tank's order (`bm.a(int, go, ...)`), or
        /// <see cref="ContactKind.None"/>.
        /// </summary>
        /// <param name="inFlight">
        /// Still short of the target: then coming within the radius of the ground is a touch, not
        /// only going under it.
        /// </param>
        /// <param name="left">The move to here took the sphere out of a building, which is then tested too.</param>
        /// <param name="previous">The cell the sphere left.</param>
        /// <param name="objects">The server's objects to test, placed in the world's flat frame.</param>
        public (ContactKind Kind, uint ObjectId) Touches(CellPoint at, float radius, bool inFlight, bool left, uint previous,
            IReadOnlyList<Obstacle> objects)
        {
            if (at.IsIndoors)
                return TouchesIndoors(at, radius, objects);

            if (!ToOutdoors(at.Cell, at.Point, out CellPoint outdoors))
                return (ContactKind.Unknown, 0);

            LandblockGeometry block = Loaded(outdoors.Cell);
            if (block == null)
                return (ContactKind.Unknown, 0);

            Vector3 p = outdoors.Point;
            IReadOnlyList<Obstacle> near = block.Near(p, radius, Scratch);
            for (int i = 0; i < near.Count; i++)
            {
                Obstacle building = near[i];
                if (building.IsBuilding && building.Touches(p, radius))
                    return (ContactKind.Static, 0);
            }

            if (!block.TryGetGround(p, out Plane ground))
                return (ContactKind.Ground, 0);

            float depth = block.WaterDepth(p);
            if (depth != 0 && Math.Abs(ground.Normal.Z) > 0.0002f)
                ground = new Plane(ground.Normal, ground.D + (depth * ground.Normal.Z));

            float above = Plane.DotCoordinate(ground, p);
            if (above <= 0 || (inFlight && above <= radius))
                return (ContactKind.Ground, 0);

            if (TouchesStatics(outdoors, block, near, radius))
                return (ContactKind.Static, 0);

            if (TouchesObjects(outdoors.Flat, radius, objects) is uint hit)
                return (ContactKind.Object, hit);

            for (int i = 0; i < near.Count; i++)
            {
                Obstacle building = near[i];
                if (!building.IsBuilding || !SolidTree.SpheresMeet(building.BoundsCentre, building.BoundsRadius, p, radius))
                    continue;

                foreach (IndoorCell room in building.DoorwayRooms)
                {
                    if (TouchesCell(room, p, radius) is ContactKind kind)
                        return (kind, 0);
                }
            }

            if (left)
            {
                IndoorCell before = Indoor(previous);
                if (before == null)
                    return (ContactKind.Unknown, 0);

                Vector3 there = InLandblock(previous, outdoors.Flat);
                if (TouchesCell(before, there, radius) is ContactKind kind)
                    return (kind, 0);
                foreach ((Plane _, uint other) in before.Doorways)
                {
                    if (Indoor(other) is IndoorCell beyond && TouchesCell(beyond, there, radius) is ContactKind beyondKind)
                        return (beyondKind, 0);
                }
            }

            return (ContactKind.None, 0);
        }

        /// <summary>A point of the world's flat frame in a landblock's frame.</summary>
        private static Vector3 InLandblock(uint cell, Vector3 flat)
            => new Vector3(flat.X - ((cell >> 24) * 192f), flat.Y - (((cell >> 16) & 0xFF) * 192f), flat.Z);

        private (ContactKind, uint) TouchesIndoors(CellPoint at, float radius, IReadOnlyList<Obstacle> objects)
        {
            IndoorCell cell = Indoor(at.Cell);
            if (cell == null)
                return (ContactKind.Unknown, 0);

            if (TouchesCell(cell, at.Point, radius) is ContactKind kind)
                return (kind, 0);

            foreach ((Plane _, uint other) in cell.Doorways)
            {
                if (Indoor(other) is IndoorCell beyond && TouchesCell(beyond, at.Point, radius) is ContactKind beyondKind)
                    return (beyondKind, 0);
            }

            if (TouchesObjects(at.Flat, radius, objects) is uint hit)
                return (ContactKind.Object, hit);

            if (cell.SeenOutside && ToOutdoors(at.Cell, at.Point, out CellPoint outdoors) && Loaded(outdoors.Cell) is LandblockGeometry block
                && TouchesStatics(outdoors, block, block.Near(outdoors.Point, radius, Scratch), radius))
                return (ContactKind.Static, 0);

            return (ContactKind.None, 0);
        }

        /// <summary>
        /// What a sphere touches of one indoor cell, if it reaches into the cell at all: its walls,
        /// then its furniture (`b9.a(go, ...)`); null for nothing.
        /// </summary>
        private static ContactKind? TouchesCell(IndoorCell cell, Vector3 point, float radius)
        {
            Vector3 inside = cell.Placement.ToInner(point);
            if (!cell.Room.Space.Reaches(inside, radius))
                return null;
            if (cell.Room.Walls.Touches(inside, radius))
                return ContactKind.Wall;

            for (int i = 0; i < cell.Statics.Count; i++)
            {
                Obstacle furniture = cell.Statics[i];
                if (furniture.Touches(point, radius))
                    return ContactKind.Static;
            }

            return null;
        }

        /// <summary>
        /// Whether a sphere touches the landscape's static objects and scenery - not buildings,
        /// which are tested first - of its own landblock (<paramref name="near"/>) and of any
        /// beside it reaching over the edge it is near.
        /// </summary>
        private bool TouchesStatics(CellPoint outdoors, LandblockGeometry block, IReadOnlyList<Obstacle> near, float radius)
        {
            Vector3 p = outdoors.Point;
            for (int i = 0; i < near.Count; i++)
            {
                Obstacle placed = near[i];
                if (!placed.IsBuilding && placed.Touches(p, radius))
                    return true;
            }

            float reach = _overhang + radius;
            if (p.X > reach && p.Y > reach && p.X < 192f - reach && p.Y < 192f - reach)
                return false;

            uint lx = outdoors.Cell >> 24, ly = (outdoors.Cell >> 16) & 0xFF;
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    long nx = lx + dx, ny = ly + dy;
                    if ((dx == 0 && dy == 0) || nx < 0 || ny < 0 || nx > 254 || ny > 254)
                        continue;

                    LandblockGeometry beside = Loaded((uint)((nx << 24) | (ny << 16)));
                    if (beside == null)
                        continue;

                    Vector3 there = p - new Vector3(dx * 192f, dy * 192f, 0);
                    float over = beside.Overhang + radius;
                    if (there.X < -over || there.Y < -over || there.X > 192f + over || there.Y > 192f + over)
                        continue;

                    IReadOnlyList<Obstacle> reaching = beside.Near(there, radius, BesideScratch);
                    for (int i = 0; i < reaching.Count; i++)
                    {
                        Obstacle placed = reaching[i];
                        if (!placed.IsBuilding && placed.Touches(there, radius))
                            return true;
                    }
                }
            }

            return false;
        }

        private static uint? TouchesObjects(Vector3 flat, float radius, IReadOnlyList<Obstacle> objects)
        {
            if (objects == null)
                return null;

            for (int i = 0; i < objects.Count; i++)
            {
                Obstacle obj = objects[i];
                if (obj.Touches(flat, radius))
                    return obj.ObjectId;
            }

            return null;
        }

        // ------------------------------------------------------------------- sweeping

        /// <summary>
        /// Sends a sphere from <paramref name="start"/> through <paramref name="moves"/>, testing it
        /// after each, and answers the first thing it met: Virindi Tank's projectile loop
        /// (`cz.a`), its path worked out by the caller. The first
        /// <paramref name="movesInFlight"/> tests are short of the target, where the ground is
        /// kept at a radius's distance.
        /// </summary>
        public SweepContact Sweep(CellPoint start, IReadOnlyList<Vector3> moves, int movesInFlight, float radius, IReadOnlyList<Obstacle> objects)
        {
            CellPoint at = start;
            for (int i = 0; i < moves.Count; i++)
            {
                at = Move(at, moves[i], out bool left, out uint previous);
                (ContactKind kind, uint id) = Touches(at, radius, i < movesInFlight, left, previous, objects);
                if (kind != ContactKind.None)
                    return new SweepContact(i, kind, id);
            }

            return SweepContact.Clear;
        }
    }
}
