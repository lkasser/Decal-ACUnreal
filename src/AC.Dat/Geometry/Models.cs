using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace AC.Dat.Geometry
{
    /// <summary>
    /// One model's solid surface (a GfxObj, file 0x01......): its physics polygons and their BSP
    /// tree. A model drawn but not solid - a banner, a plant - has none.
    /// </summary>
    public sealed class SolidModel
    {
        private const uint HasPhysics = 0x1;

        private SolidModel(uint id, SolidTree solid)
        {
            Id = id;
            Solid = solid;
        }

        public uint Id { get; }

        /// <summary>The surface, or null for a model with nothing solid.</summary>
        public SolidTree Solid { get; }

        /// <summary>
        /// Reads a GfxObj as far as its physics: the id and flags, the surfaces (a packed count of
        /// words), the vertices, then - with the physics flag - the physics polygons (a packed
        /// count of them, each with its id) and the physics tree. Null when the bytes do not read.
        /// </summary>
        public static SolidModel Parse(byte[] data)
        {
            if (data == null)
                return null;

            try
            {
                ModelReader reader = new ModelReader(data);
                uint id = reader.ReadUInt32();
                uint flags = reader.ReadUInt32();
                reader.Skip((int)reader.ReadCompressedUInt32() * 4);
                Dictionary<ushort, Vector3> vertices = SolidTree.ReadVertices(reader);

                if ((flags & HasPhysics) == 0)
                    return new SolidModel(id, null);

                int count = (int)reader.ReadCompressedUInt32();
                Dictionary<ushort, SolidPolygon> polygons = SolidTree.ReadPolygons(reader, count, vertices);
                BspNode root = BspNode.Read(reader, BspKind.Physics);
                return new SolidModel(id, new SolidTree(root, polygons));
            }
            catch (InvalidDataException)
            {
                return null;
            }
        }
    }

    /// <summary>A vertical cylinder with rounded ends: a creature's body, a tree's trunk.</summary>
    public readonly struct CylinderSphere
    {
        public CylinderSphere(Vector3 low, float radius, float height)
        {
            Low = low;
            Radius = radius;
            Height = height;
        }

        /// <summary>The middle of its foot, in the object's frame.</summary>
        public Vector3 Low { get; }

        public float Radius { get; }

        public float Height { get; }
    }

    /// <summary>
    /// An object's make-up (a Setup, file 0x02......): its parts and where they stand at rest, and
    /// the cylinders and spheres the game tests an object without a solid surface by.
    /// </summary>
    public sealed class ObjectSetup
    {
        private const uint HasParent = 0x1, HasDefaultScale = 0x2, HasPhysicsBsp = 0x8;

        /// <summary>The placement the game puts an object's parts in when nothing animates them: Resting.</summary>
        private const int RestingPlacement = 0x65;

        private ObjectSetup()
        {
        }

        public uint Id { get; private set; }

        /// <summary>Whether the parts' solid surfaces are what is hit, rather than the cylinders and spheres.</summary>
        public bool UsesParts { get; private set; }

        /// <summary>The parts' models (GfxObj ids).</summary>
        public IReadOnlyList<uint> Parts { get; private set; } = Array.Empty<uint>();

        /// <summary>Each part's size, as a factor of the object's.</summary>
        public IReadOnlyList<float> PartScales { get; private set; } = Array.Empty<float>();

        /// <summary>Each part's placement in the object at rest.</summary>
        public IReadOnlyList<Placement> PartPlacements { get; private set; } = Array.Empty<Placement>();

        public IReadOnlyList<CylinderSphere> CylinderSpheres { get; private set; } = Array.Empty<CylinderSphere>();

        /// <summary>Spheres, each its centre and radius, in the object's frame.</summary>
        public IReadOnlyList<(Vector3 Centre, float Radius)> Spheres { get; private set; } = Array.Empty<(Vector3, float)>();

        /// <summary>The object's height, at its own size: what the game aims at fractions of.</summary>
        public float Height { get; private set; }

        public float Radius { get; private set; }

        /// <summary>A sphere round the drawn object, in its frame.</summary>
        public (Vector3 Centre, float Radius) SortingSphere { get; private set; }

        /// <summary>
        /// Reads a Setup: the id and flags; the parts; their parents and sizes, where the flags say;
        /// the holding and connection points; the placements, each a frame per part and its
        /// animation hooks; the cylinders and spheres; then the height, the radius and the spheres
        /// round it. What follows - lights, default animations - is not needed. Null when the
        /// bytes do not read.
        /// </summary>
        public static ObjectSetup Parse(byte[] data)
        {
            if (data == null)
                return null;

            try
            {
                ModelReader reader = new ModelReader(data);
                ObjectSetup setup = new ObjectSetup { Id = reader.ReadUInt32() };
                uint flags = reader.ReadUInt32();
                setup.UsesParts = (flags & HasPhysicsBsp) != 0;

                int parts = reader.ReadCount(1024);
                uint[] ids = new uint[parts];
                for (int i = 0; i < parts; i++)
                    ids[i] = reader.ReadUInt32();
                setup.Parts = ids;

                if ((flags & HasParent) != 0)
                    reader.Skip(parts * 4);

                float[] scales = new float[parts];
                for (int i = 0; i < parts; i++)
                    scales[i] = 1f;
                if ((flags & HasDefaultScale) != 0)
                {
                    for (int i = 0; i < parts; i++)
                    {
                        // The game sizes a part's solid surface by the third of its three factors.
                        Vector3 scale = reader.ReadVector3();
                        scales[i] = scale.Z;
                    }
                }

                setup.PartScales = scales;

                SkipLocations(reader);
                SkipLocations(reader);

                // Resting if the setup has it, else the default placement, else whichever came first.
                Placement[] resting = null, standing = null, any = null;
                int placements = reader.ReadCount(1024);
                for (int p = 0; p < placements; p++)
                {
                    int key = reader.ReadInt32();
                    Placement[] frames = new Placement[parts];
                    for (int i = 0; i < parts; i++)
                        frames[i] = reader.ReadPlacement();
                    SkipHooks(reader);

                    if (key == RestingPlacement)
                        resting = frames;
                    else if (key == 0)
                        standing = frames;
                    any ??= frames;
                }

                setup.PartPlacements = resting ?? standing ?? any ?? Identities(parts);

                int cylinders = reader.ReadCount(1024);
                CylinderSphere[] cyl = new CylinderSphere[cylinders];
                for (int i = 0; i < cylinders; i++)
                    cyl[i] = new CylinderSphere(reader.ReadVector3(), reader.ReadSingle(), reader.ReadSingle());
                setup.CylinderSpheres = cyl;

                int spheres = reader.ReadCount(1024);
                (Vector3, float)[] sph = new (Vector3, float)[spheres];
                for (int i = 0; i < spheres; i++)
                    sph[i] = (reader.ReadVector3(), reader.ReadSingle());
                setup.Spheres = sph;

                setup.Height = reader.ReadSingle();
                setup.Radius = reader.ReadSingle();
                reader.ReadSingle(); // step up
                reader.ReadSingle(); // step down
                setup.SortingSphere = (reader.ReadVector3(), reader.ReadSingle());
                return setup;
            }
            catch (InvalidDataException)
            {
                return null;
            }
        }

        /// <summary>A setup of one part, for an object whose model is a bare GfxObj.</summary>
        public static ObjectSetup OfOneModel(uint gfxObjId) => new ObjectSetup
        {
            Id = gfxObjId,
            UsesParts = true,
            Parts = new[] { gfxObjId },
            PartScales = new[] { 1f },
            PartPlacements = new[] { Placement.Identity },
        };

        private static Placement[] Identities(int count)
        {
            Placement[] frames = new Placement[count];
            for (int i = 0; i < count; i++)
                frames[i] = Placement.Identity;
            return frames;
        }

        /// <summary>Holding or connection points: a count, then each its key, its part and its frame.</summary>
        private static void SkipLocations(ModelReader reader)
        {
            int count = reader.ReadCount(1024);
            reader.Skip(count * (4 + 4 + 28));
        }

        /// <summary>
        /// A placement's animation hooks: a count, then each hook's kind and direction and what its
        /// kind carries. Rare in a placement, but each must be stepped over to reach what follows.
        /// </summary>
        private static void SkipHooks(ModelReader reader)
        {
            int count = reader.ReadCount(1024);
            for (int i = 0; i < count; i++)
            {
                uint kind = reader.ReadUInt32();
                reader.ReadInt32(); // the direction
                switch (kind)
                {
                    case 0:   // no operation
                    case 4:   // animation done
                    case 17:  // default script
                        break;
                    case 1:   // sound
                    case 2:   // sound table
                    case 6:   // ethereal
                    case 14:  // destroy particle
                    case 15:  // stop particle
                    case 16:  // no draw
                    case 18:  // default script part
                    case 25:  // set light
                        reader.Skip(4);
                        break;
                    case 3:   // attack: a part and its cone
                        reader.Skip(28);
                        break;
                    case 5:   // replace object: a part, then a packed model id
                        reader.Skip(2);
                        if ((reader.ReadUInt16() & 0x8000) != 0)
                            reader.Skip(2);
                        break;
                    case 7:   // transparent part
                    case 9:   // luminous part
                    case 11:  // diffuse part
                    case 21:  // sound tweaked
                        reader.Skip(16);
                        break;
                    case 8:   // luminous
                    case 10:  // diffuse
                    case 20:  // transparent
                    case 22:  // set omega
                    case 24:  // texture velocity part
                        reader.Skip(12);
                        break;
                    case 12:  // scale
                    case 19:  // call particle script
                    case 23:  // texture velocity
                        reader.Skip(8);
                        break;
                    case 13:  // create particle: emitter, part, frame, emitter id
                    case 26:  // create blocking particle
                        reader.Skip(4 + 4 + 28 + 4);
                        break;
                    default:
                        throw new InvalidDataException($"An animation hook of kind {kind} is no kind known.");
                }
            }
        }
    }

    /// <summary>
    /// The shape of one kind of room (an Environment's cell structure): the BSP of its space, and its
    /// walls, floors and ceilings with theirs; and the polygons its doorways are cut in.
    /// </summary>
    public sealed class RoomShape
    {
        internal RoomShape(CellSpace space, SolidTree walls, Dictionary<ushort, Plane> portalPlanes)
        {
            Space = space;
            Walls = walls;
            PortalPlanes = portalPlanes;
        }

        public CellSpace Space { get; }

        public SolidTree Walls { get; }

        /// <summary>The planes of the room's drawn polygons that are doorways, by polygon id.</summary>
        public IReadOnlyDictionary<ushort, Plane> PortalPlanes { get; }

        /// <summary>
        /// Reads one cell structure: the counts of polygons, physics polygons and doorways; the
        /// vertices; the drawn polygons; the doorways' polygon ids; the space's tree; the physics
        /// polygons and their tree. The drawing tree after it is not needed.
        /// </summary>
        internal static RoomShape Read(ModelReader reader)
        {
            int polygons = reader.ReadCount(1 << 16);
            int physics = reader.ReadCount(1 << 16);
            int portals = reader.ReadCount(1 << 16);
            Dictionary<ushort, Vector3> vertices = SolidTree.ReadVertices(reader);

            Dictionary<ushort, SolidPolygon> drawn = SolidTree.ReadPolygons(reader, polygons, vertices);
            Dictionary<ushort, Plane> portalPlanes = new Dictionary<ushort, Plane>(portals);
            for (int i = 0; i < portals; i++)
            {
                ushort id = reader.ReadUInt16();
                if (drawn.TryGetValue(id, out SolidPolygon portal))
                    portalPlanes[id] = portal.Plane;
            }

            reader.Align();
            CellSpace space = new CellSpace(BspNode.Read(reader, BspKind.Cell));
            Dictionary<ushort, SolidPolygon> solid = SolidTree.ReadPolygons(reader, physics, vertices);
            BspNode root = BspNode.Read(reader, BspKind.Physics);
            return new RoomShape(space, new SolidTree(root, solid), portalPlanes);
        }
    }

    /// <summary>An Environment (file 0x0D......): the shapes of the rooms indoor cells are built of, by index.</summary>
    public sealed class RoomShapes
    {
        private readonly Dictionary<uint, RoomShape> _rooms;

        private RoomShapes(uint id, Dictionary<uint, RoomShape> rooms)
        {
            Id = id;
            _rooms = rooms;
        }

        public uint Id { get; }

        public RoomShape Get(uint index) => _rooms.TryGetValue(index, out RoomShape room) ? room : null;

        /// <summary>
        /// Reads an Environment, all its rooms. Each room ends on a word boundary of the file, after
        /// a drawing tree this does not keep, so the rooms are read in order and the drawing tree of
        /// each is read through rather than skipped. Null when the bytes do not read.
        /// </summary>
        public static RoomShapes Parse(byte[] data)
        {
            if (data == null)
                return null;

            try
            {
                ModelReader reader = new ModelReader(data);
                uint id = reader.ReadUInt32();
                int count = reader.ReadCount(1 << 16);
                Dictionary<uint, RoomShape> rooms = new Dictionary<uint, RoomShape>(count);
                for (int i = 0; i < count; i++)
                {
                    uint index = reader.ReadUInt32();
                    rooms[index] = RoomShape.Read(reader);
                    if (reader.ReadUInt32() != 0)
                        BspNode.Read(reader, BspKind.Drawing);
                    reader.Align();
                }

                return new RoomShapes(id, rooms);
            }
            catch (InvalidDataException)
            {
                return null;
            }
        }
    }
}
