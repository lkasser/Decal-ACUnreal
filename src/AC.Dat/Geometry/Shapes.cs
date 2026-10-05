using System;
using System.Collections.Generic;
using System.Numerics;

namespace AC.Dat.Geometry
{
    /// <summary>
    /// What a projectile can hit of one kind of object: its parts' solid surfaces, or else the
    /// cylinders, or else the spheres its setup gives it - the game's order, and Virindi Tank's
    /// (`du.a(go)`).
    /// </summary>
    public sealed class ObjectShape
    {
        /// <summary>The game's tolerance at a cylinder's ends.</summary>
        private const float Tolerance = 0.0002f;

        private readonly (SolidTree Tree, Placement Placement, float Scale)[] _parts;
        private readonly CylinderSphere[] _cylinders;
        private readonly (Vector3 Centre, float Radius)[] _spheres;

        private ObjectShape(uint modelId, ObjectSetup setup, (SolidTree, Placement, float)[] parts)
        {
            ModelId = modelId;
            Setup = setup;
            _parts = parts ?? Array.Empty<(SolidTree, Placement, float)>();
            bool usesParts = setup == null || setup.UsesParts;
            _cylinders = usesParts || setup == null ? Array.Empty<CylinderSphere>() : ToArray(setup.CylinderSpheres);
            _spheres = usesParts || setup == null || _cylinders.Length > 0 ? Array.Empty<(Vector3, float)>() : ToArray(setup.Spheres);
            if (!usesParts)
                _parts = Array.Empty<(SolidTree, Placement, float)>();
            (BoundsCentre, BoundsRadius) = Bounds();
        }

        /// <summary>The Setup or GfxObj the shape was made from.</summary>
        public uint ModelId { get; }

        /// <summary>The setup, or null for an object that is one bare model.</summary>
        public ObjectSetup Setup { get; }

        /// <summary>The object's height at its own size, as its setup gives it; 0 for a bare model.</summary>
        public float Height => Setup?.Height ?? 0f;

        /// <summary>A sphere round everything that can be hit, in the object's frame at its own size.</summary>
        public Vector3 BoundsCentre { get; }

        public float BoundsRadius { get; }

        /// <summary>Whether there is nothing to hit at all.</summary>
        public bool IsEmpty => _parts.Length == 0 && _cylinders.Length == 0 && _spheres.Length == 0;

        /// <summary>The shape of a setup, its parts' solid models found by <paramref name="models"/>.</summary>
        public static ObjectShape Of(ObjectSetup setup, Func<uint, SolidModel> models)
        {
            if (setup == null)
                return null;

            List<(SolidTree, Placement, float)> parts = new List<(SolidTree, Placement, float)>();
            if (setup.UsesParts)
            {
                for (int i = 0; i < setup.Parts.Count; i++)
                {
                    SolidTree tree = models?.Invoke(setup.Parts[i])?.Solid;
                    if (tree == null)
                        continue;

                    Placement at = i < setup.PartPlacements.Count ? setup.PartPlacements[i] : Placement.Identity;
                    float scale = i < setup.PartScales.Count ? setup.PartScales[i] : 1f;
                    parts.Add((tree, at, scale == 0 ? 1f : scale));
                }
            }

            return new ObjectShape(setup.Id, setup, parts.ToArray());
        }

        /// <summary>The shape of an object that is a single model.</summary>
        public static ObjectShape OfModel(SolidModel model)
        {
            if (model == null)
                return null;

            (SolidTree, Placement, float)[] parts = model.Solid == null
                ? Array.Empty<(SolidTree, Placement, float)>()
                : new[] { (model.Solid, Placement.Identity, 1f) };
            return new ObjectShape(model.Id, null, parts);
        }

        /// <summary>
        /// Whether a sphere touches the object standing at <paramref name="at"/> at
        /// <paramref name="scale"/> times its size; the sphere's centre is in the frame
        /// <paramref name="at"/> is given in.
        /// </summary>
        public bool Touches(Placement at, float scale, Vector3 centre, float radius)
        {
            if (scale <= 0)
                scale = 1f;

            Vector3 local = at.ToInner(centre);
            if (!SolidTree.SpheresMeet(BoundsCentre * scale, BoundsRadius * scale, local, radius))
                return false;

            foreach ((SolidTree tree, Placement part, float partScale) in _parts)
            {
                float size = partScale * scale;
                Vector3 inPart = Vector3.Transform(local - (part.Origin * scale), Quaternion.Conjugate(part.Orientation)) / size;
                if (tree.Touches(inPart, radius / size))
                    return true;
            }

            foreach (CylinderSphere cylinder in _cylinders)
            {
                Vector3 low = cylinder.Low * scale;
                float r = cylinder.Radius * scale;
                float h = cylinder.Height * scale;
                float dx = local.X - low.X, dy = local.Y - low.Y, dz = local.Z - low.Z;
                float across = radius + r;
                if ((dx * dx) + (dy * dy) <= across * across && Math.Abs((0.5f * h) - dz) <= radius - Tolerance + (0.5f * h))
                    return true;
            }

            foreach ((Vector3 c, float r) in _spheres)
            {
                if (SolidTree.SpheresMeet(c * scale, r * scale, local, radius))
                    return true;
            }

            return false;
        }

        /// <summary>One sphere round every part, cylinder and sphere.</summary>
        private (Vector3, float) Bounds()
        {
            List<(Vector3 Centre, float Radius)> all = new List<(Vector3, float)>();
            foreach ((SolidTree tree, Placement part, float partScale) in _parts)
            {
                if (float.IsInfinity(tree.BoundsRadius))
                    return (Vector3.Zero, float.PositiveInfinity);
                all.Add((part.ToOuter(tree.BoundsCentre * partScale), tree.BoundsRadius * partScale));
            }

            foreach (CylinderSphere cylinder in _cylinders)
            {
                float half = cylinder.Height / 2f;
                all.Add((cylinder.Low + new Vector3(0, 0, half), MathF.Sqrt((cylinder.Radius * cylinder.Radius) + (half * half))));
            }

            foreach ((Vector3 c, float r) in _spheres)
                all.Add((c, r));

            if (all.Count == 0)
                return (Vector3.Zero, 0f);

            Vector3 centre = Vector3.Zero;
            foreach ((Vector3 c, float _) in all)
                centre += c;
            centre /= all.Count;

            float radius = 0f;
            foreach ((Vector3 c, float r) in all)
                radius = Math.Max(radius, Vector3.Distance(centre, c) + r);
            return (centre, radius);
        }

        private static T[] ToArray<T>(IReadOnlyList<T> list)
        {
            if (list == null || list.Count == 0)
                return Array.Empty<T>();
            T[] array = new T[list.Count];
            for (int i = 0; i < array.Length; i++)
                array[i] = list[i];
            return array;
        }
    }

    /// <summary>
    /// Something standing in the world that a projectile can hit: a shape, where it stands, how
    /// big it is, and a sphere round it there for a quick first look.
    /// </summary>
    public sealed class Obstacle
    {
        public Obstacle(ObjectShape shape, Placement placement, float scale = 1f, uint objectId = 0)
        {
            Shape = shape ?? throw new ArgumentNullException(nameof(shape));
            Placement = placement;
            Scale = scale <= 0 ? 1f : scale;
            ObjectId = objectId;
            BoundsCentre = placement.ToOuter(shape.BoundsCentre * Scale);
            BoundsRadius = shape.BoundsRadius * Scale;
        }

        public ObjectShape Shape { get; }

        /// <summary>Where it stands: in its landblock for the client's own objects, in the world's flat frame for the server's.</summary>
        public Placement Placement { get; }

        public float Scale { get; }

        /// <summary>The server's id for it; 0 for one the client places itself.</summary>
        public uint ObjectId { get; }

        /// <summary>A building's shell, which Virindi Tank tested before the ground.</summary>
        public bool IsBuilding { get; internal set; }

        /// <summary>For a building, the indoor cells its doorways lead into.</summary>
        public IReadOnlyList<uint> DoorwayCells { get; internal set; } = Array.Empty<uint>();

        /// <summary>For a building, those cells as read.</summary>
        internal IndoorCell[] DoorwayRooms { get; set; } = Array.Empty<IndoorCell>();

        /// <summary>For a building, the cells a point coming in from outside may be in: those its doorways lead into and those seen from them.</summary>
        internal IndoorCell[] Rooms { get; set; } = Array.Empty<IndoorCell>();

        public Vector3 BoundsCentre { get; }

        public float BoundsRadius { get; }

        public bool Touches(Vector3 centre, float radius)
            => SolidTree.SpheresMeet(BoundsCentre, BoundsRadius, centre, radius) && Shape.Touches(Placement, Scale, centre, radius);

        public override string ToString() => $"0x{Shape.ModelId:X8}{(ObjectId != 0 ? $" (0x{ObjectId:X8})" : string.Empty)} at {Placement.Origin}";
    }
}
