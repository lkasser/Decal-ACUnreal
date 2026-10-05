using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace AC.Dat.Geometry
{
    /// <summary>A flat polygon of a model's solid surface, its corners in the model's frame.</summary>
    public sealed class SolidPolygon
    {
        /// <summary>The game's tolerance for touching: a sphere grazing a face by less does not count.</summary>
        private const float Tolerance = 0.0002f;

        public SolidPolygon(Vector3[] corners)
        {
            Corners = corners ?? Array.Empty<Vector3>();
            Plane = PlaneThrough(Corners);
        }

        public IReadOnlyList<Vector3> Corners { get; }

        /// <summary>The polygon's plane, facing the way its corners turn anticlockwise.</summary>
        public Plane Plane { get; }

        /// <summary>
        /// The plane through a polygon's corners by Newell's sum, which a slightly bent polygon
        /// still gives a fair plane from, its distance term from the corners' middle.
        /// </summary>
        internal static Plane PlaneThrough(IReadOnlyList<Vector3> corners)
        {
            Vector3 normal = Vector3.Zero;
            Vector3 middle = Vector3.Zero;
            for (int i = 0; i < corners.Count; i++)
            {
                Vector3 a = corners[i];
                Vector3 b = corners[(i + 1) % corners.Count];
                normal.X += (a.Y - b.Y) * (a.Z + b.Z);
                normal.Y += (a.Z - b.Z) * (a.X + b.X);
                normal.Z += (a.X - b.X) * (a.Y + b.Y);
                middle += a;
            }

            float length = normal.Length();
            if (length < 1e-12f || corners.Count == 0)
                return new Plane(Vector3.UnitZ, 0f);

            normal /= length;
            middle /= corners.Count;
            return new Plane(normal, -Vector3.Dot(normal, middle));
        }

        /// <summary>
        /// Whether a sphere touches the polygon: its centre within the radius of the face, over the
        /// polygon, or within the radius of an edge or a corner.
        /// </summary>
        public bool Touches(Vector3 centre, float radius)
        {
            if (Corners.Count < 3)
                return false;

            float reach = radius - Tolerance;
            float distance = Plane.DotCoordinate(Plane, centre);
            if (Math.Abs(distance) > reach)
                return false;

            Vector3 normal = Plane.Normal;
            Vector3 over = centre - (normal * distance);

            bool inside = true;
            for (int i = 0; i < Corners.Count && inside; i++)
            {
                Vector3 a = Corners[i];
                Vector3 b = Corners[(i + 1) % Corners.Count];
                if (Vector3.Dot(Vector3.Cross(b - a, over - a), normal) < -Tolerance)
                    inside = false;
            }

            if (inside)
                return true;

            float reachSquared = reach * reach;
            for (int i = 0; i < Corners.Count; i++)
            {
                Vector3 a = Corners[i];
                Vector3 b = Corners[(i + 1) % Corners.Count];
                Vector3 edge = b - a;
                float along = edge.LengthSquared() <= 0 ? 0 : Math.Clamp(Vector3.Dot(centre - a, edge) / edge.LengthSquared(), 0f, 1f);
                if (Vector3.DistanceSquared(centre, a + (edge * along)) <= reachSquared)
                    return true;
            }

            return false;
        }
    }

    /// <summary>Which of the three kinds of tree a model's BSP is, for what each node carries.</summary>
    internal enum BspKind
    {
        Drawing,
        Physics,
        Cell,
    }

    /// <summary>
    /// One node of a BSP tree: a splitting plane with the side in front and the side behind, or a
    /// leaf. A physics tree's nodes carry a sphere round all beneath them, and its leaves the
    /// polygons in them and whether they are solid.
    /// </summary>
    internal sealed class BspNode
    {
        // The node tags, as the file has them: four characters, last first.
        private const uint Leaf = 0x4C454146;     // "LEAF"
        private const uint Portal = 0x504F5254;   // "PORT"
        private const uint FrontOnly1 = 0x4250_6E6E; // "BPnn"
        private const uint FrontOnly2 = 0x4250_496E; // "BPIn"
        private const uint BackOnly1 = 0x4270_494E;  // "BpIN"
        private const uint BackOnly2 = 0x4270_6E4E;  // "BpnN"
        private const uint Both1 = 0x4250_494E;      // "BPIN"
        private const uint Both2 = 0x4250_6E4E;      // "BPnN"

        /// <summary>Deep enough for any tree the client ships; a misread goes no deeper.</summary>
        private const int MaxDepth = 1024;

        public bool IsLeaf;
        public Plane Plane;
        public BspNode Front;
        public BspNode Back;

        /// <summary>A sphere round the node and all beneath it; a radius of infinity where the tree has none.</summary>
        public Vector3 BoundsCentre;
        public float BoundsRadius = float.PositiveInfinity;

        /// <summary>A physics leaf the inside of a closed shape.</summary>
        public bool Solid;

        /// <summary>A physics leaf's polygons, by their ids in the model.</summary>
        public ushort[] Polygons = Array.Empty<ushort>();

        public static BspNode Read(ModelReader reader, BspKind kind) => Read(reader, kind, 0);

        private static BspNode Read(ModelReader reader, BspKind kind, int depth)
        {
            if (depth > MaxDepth)
                throw new InvalidDataException("A BSP tree nests past belief.");

            uint tag = reader.ReadUInt32();
            BspNode node = new BspNode();

            if (tag == Leaf)
            {
                node.IsLeaf = true;
                reader.ReadInt32(); // the leaf's index
                if (kind == BspKind.Physics)
                {
                    node.Solid = reader.ReadInt32() != 0;
                    node.BoundsCentre = reader.ReadVector3();
                    node.BoundsRadius = reader.ReadSingle();
                    node.Polygons = ReadIds(reader);
                }

                return node;
            }

            node.Plane = reader.ReadPlane();

            if (tag == Portal)
            {
                node.Front = Read(reader, kind, depth + 1);
                node.Back = Read(reader, kind, depth + 1);
                if (kind == BspKind.Drawing)
                {
                    node.BoundsCentre = reader.ReadVector3();
                    node.BoundsRadius = reader.ReadSingle();
                    int polygons = reader.ReadCount();
                    int portals = reader.ReadCount();
                    reader.Skip((polygons * 2) + (portals * 4));
                }

                return node;
            }

            switch (tag)
            {
                case FrontOnly1:
                case FrontOnly2:
                    node.Front = Read(reader, kind, depth + 1);
                    break;
                case BackOnly1:
                case BackOnly2:
                    node.Back = Read(reader, kind, depth + 1);
                    break;
                case Both1:
                case Both2:
                    node.Front = Read(reader, kind, depth + 1);
                    node.Back = Read(reader, kind, depth + 1);
                    break;

                // Any other tag - "BPFL", "BPOL" - is a plane with nothing beyond it on either side.
            }

            if (kind == BspKind.Cell)
                return node;

            node.BoundsCentre = reader.ReadVector3();
            node.BoundsRadius = reader.ReadSingle();
            if (kind == BspKind.Drawing)
                ReadIds(reader);

            return node;
        }

        private static ushort[] ReadIds(ModelReader reader)
        {
            int count = reader.ReadCount(1 << 16);
            ushort[] ids = new ushort[count];
            for (int i = 0; i < count; i++)
                ids[i] = reader.ReadUInt16();
            return ids;
        }
    }

    /// <summary>
    /// A model's solid surface: its physics polygons and the BSP tree over them, which says
    /// whether a sphere touches it.
    /// </summary>
    public sealed class SolidTree
    {
        /// <summary>Where a sphere straddles a plane by less than this, its centre's side decides.</summary>
        private const float Straddle = 0.0002f;

        private readonly BspNode _root;
        private readonly Dictionary<ushort, SolidPolygon> _polygons;

        internal SolidTree(BspNode root, Dictionary<ushort, SolidPolygon> polygons)
        {
            _root = root;
            _polygons = polygons ?? new Dictionary<ushort, SolidPolygon>();
        }

        /// <summary>A sphere round the whole surface, in the model's frame.</summary>
        public Vector3 BoundsCentre => _root?.BoundsCentre ?? Vector3.Zero;

        public float BoundsRadius => _root == null ? 0f : _root.BoundsRadius;

        public int PolygonCount => _polygons.Count;

        /// <summary>
        /// Whether a sphere, in the model's frame, touches the surface: Virindi Tank's test of a
        /// model (`h9.b`). Down the tree, every branch the sphere reaches; a leaf whose polygons
        /// the sphere touches is a touch, and so is a solid leaf the sphere's centre is in - inside
        /// a closed shape, without touching any face of it.
        /// </summary>
        public bool Touches(Vector3 centre, float radius)
            => _root != null && Touches(_root, centre, radius, centreSide: true);

        private bool Touches(BspNode node, Vector3 centre, float radius, bool centreSide)
        {
            if (node.IsLeaf)
            {
                if (centreSide && node.Solid)
                    return true;
                if (!SpheresMeet(node.BoundsCentre, node.BoundsRadius, centre, radius))
                    return false;

                foreach (ushort id in node.Polygons)
                {
                    if (_polygons.TryGetValue(id, out SolidPolygon polygon) && polygon.Touches(centre, radius))
                        return true;
                }

                return false;
            }

            if (!SpheresMeet(node.BoundsCentre, node.BoundsRadius, centre, radius))
                return false;

            float distance = Plane.DotCoordinate(node.Plane, centre);
            float reach = radius - Straddle;
            if (distance >= reach)
                return node.Front != null && Touches(node.Front, centre, radius, centreSide);
            if (distance <= -reach)
                return node.Back != null && Touches(node.Back, centre, radius, centreSide);

            bool inFront = distance >= 0;
            return (node.Front != null && Touches(node.Front, centre, radius, centreSide && inFront))
                || (node.Back != null && Touches(node.Back, centre, radius, centreSide && !inFront));
        }

        internal static bool SpheresMeet(Vector3 a, float aRadius, Vector3 b, float bRadius)
        {
            if (float.IsPositiveInfinity(aRadius))
                return true;
            float reach = aRadius + bRadius;
            return Vector3.DistanceSquared(a, b) <= reach * reach;
        }

        /// <summary>Reads a model's physics polygons as the file has them, with the vertices they index.</summary>
        internal static Dictionary<ushort, SolidPolygon> ReadPolygons(ModelReader reader, int count, Dictionary<ushort, Vector3> vertices)
        {
            Dictionary<ushort, SolidPolygon> polygons = new Dictionary<ushort, SolidPolygon>(count);
            for (int i = 0; i < count; i++)
            {
                ushort id = reader.ReadUInt16();
                polygons[id] = ReadPolygon(reader, vertices);
            }

            return polygons;
        }

        /// <summary>
        /// One polygon: the corner count, the stippling, which sides are drawn, the two surfaces,
        /// the corners' vertex ids, then texture indices for the sides that have them.
        /// </summary>
        internal static SolidPolygon ReadPolygon(ModelReader reader, Dictionary<ushort, Vector3> vertices)
        {
            const byte NoFront = 0x4, NoBack = 0x8;
            const int Clockwise = 2;

            int corners = reader.ReadByte();
            byte stippling = reader.ReadByte();
            int sides = reader.ReadInt32();
            reader.ReadInt16();
            reader.ReadInt16();

            Vector3[] points = new Vector3[corners];
            for (int i = 0; i < corners; i++)
            {
                ushort vertex = (ushort)reader.ReadInt16();
                points[i] = vertices != null && vertices.TryGetValue(vertex, out Vector3 at) ? at : Vector3.Zero;
            }

            if ((stippling & NoFront) == 0)
                reader.Skip(corners);
            if (sides == Clockwise && (stippling & NoBack) == 0)
                reader.Skip(corners);

            return new SolidPolygon(points);
        }

        /// <summary>A vertex array: its kind (1 is the only one), a count, then each vertex with its texture points.</summary>
        internal static Dictionary<ushort, Vector3> ReadVertices(ModelReader reader)
        {
            int type = reader.ReadInt32();
            int count = reader.ReadCount(1 << 16);
            if (type != 1)
                throw new InvalidDataException($"A vertex array of kind {type} is no kind known.");

            Dictionary<ushort, Vector3> vertices = new Dictionary<ushort, Vector3>(count);
            for (int i = 0; i < count; i++)
            {
                ushort id = reader.ReadUInt16();
                ushort uvs = reader.ReadUInt16();
                Vector3 origin = reader.ReadVector3();
                reader.Skip(12 + (uvs * 8)); // the normal, then the texture points
                vertices[id] = origin;
            }

            return vertices;
        }
    }

    /// <summary>
    /// An indoor cell's shape of space: the BSP tree whose front sides enclose the room, which
    /// says whether a point or a sphere is in it.
    /// </summary>
    public sealed class CellSpace
    {
        private const float PointTolerance = 0.0002f;
        private const float SphereTolerance = 0.01f;

        private readonly BspNode _root;

        internal CellSpace(BspNode root)
        {
            _root = root;
        }

        /// <summary>
        /// Whether a point, in the cell's frame, is in it: in front of or on every plane down the
        /// front branches (Virindi Tank's `dd.a(gi)`).
        /// </summary>
        public bool Contains(Vector3 point)
        {
            BspNode node = _root;
            while (node != null && !node.IsLeaf)
            {
                if (Plane.DotCoordinate(node.Plane, point) < -PointTolerance)
                    return false;
                node = node.Front;
            }

            return _root != null;
        }

        /// <summary>
        /// Whether a sphere, in the cell's frame, reaches into it: not wholly behind any plane down
        /// the front branches (Virindi Tank's `dd.a(go)`).
        /// </summary>
        public bool Reaches(Vector3 centre, float radius)
        {
            float reach = radius + SphereTolerance;
            BspNode node = _root;
            while (node != null && !node.IsLeaf)
            {
                if (Plane.DotCoordinate(node.Plane, centre) <= -reach)
                    return false;
                node = node.Front;
            }

            return _root != null;
        }
    }
}
