using System;
using System.Collections.Generic;
using System.Numerics;

namespace AC.Dat.Geometry
{
    /// <summary>
    /// The trees, bushes and rocks the client scatters over the landscape by itself. No file lists
    /// them: each vertex's terrain names a scene type, and a hash of the vertex's place in the world
    /// picks the scene, which of its objects stand, and where, how turned and how big. The hashes
    /// are the client's own, so every client - and ACE, which works them out the same way for its
    /// collisions - puts the same tree in the same place.
    /// </summary>
    internal static class Scenery
    {
        private const double ToUnit = 1.0 / 4294967296.0;
        private const float Tile = 24f;

        /// <summary>Places the scenery of a landblock whose ground and buildings are known.</summary>
        public static void Place(LandblockGeometry block, RegionInfo region, Func<uint, IReadOnlyList<SceneryObject>> scenes,
            Func<uint, ObjectShape> shapes, Func<int, bool> cellHasBuilding)
        {
            if (region == null || region.TerrainSceneTypes.Count == 0)
                return;

            uint gx0 = (block.Id >> 24) * 8, gy0 = ((block.Id >> 16) & 0xFF) * 8;
            for (int vertex = 0; vertex < 81; vertex++)
            {
                ushort terrain = block.Terrain[vertex];
                int type = (terrain >> 2) & 0x1F;
                int sceneNumber = terrain >> 11;
                if (type >= region.TerrainSceneTypes.Count || sceneNumber >= region.TerrainSceneTypes[type].Length)
                    continue;

                uint sceneType = region.TerrainSceneTypes[type][sceneNumber];
                if (sceneType >= region.SceneTypeScenes.Count)
                    continue;

                uint[] choices = region.SceneTypeScenes[(int)sceneType];
                if (choices.Length == 0)
                    continue;

                uint vx = (uint)(vertex / 9), vy = (uint)(vertex % 9);
                uint x = vx + gx0, y = vy + gy0;
                uint pick = unchecked((y * ((712977289u * x) + 1813693831u)) - (1109124029u * x) + 2139937281u);
                int index = (int)(choices.Length * (pick * ToUnit));
                if (index >= choices.Length)
                    index = 0;

                IReadOnlyList<SceneryObject> objects = scenes(choices[index]);
                if (objects == null)
                    continue;

                uint fromX = unchecked(0u - (1109124029u * x)), fromY = unchecked(1813693831u * y);
                uint seed = unchecked((1360117743u * x * y) + 1888038839u);
                for (uint k = 0; k < objects.Count; k++)
                {
                    SceneryObject obj = objects[(int)k];
                    double chance = unchecked(fromX + fromY - (seed * (23399u + k))) * ToUnit;
                    if (!(chance < obj.Frequency) || obj.WeenieObj != 0)
                        continue;

                    Vector3 shift = Displace(obj, x, y, k);
                    float lx = (vx * Tile) + shift.X, ly = (vy * Tile) + shift.Y;
                    if (lx < 0 || ly < 0 || lx >= 192 || ly >= 192 || OnRoad(block, region.RoadWidth, lx, ly))
                        continue;

                    int cell = ((int)(lx / Tile) * 8) + (int)(ly / Tile);
                    if (cellHasBuilding(cell))
                        continue;

                    Vector3 at = new Vector3(lx, ly, shift.Z);
                    if (!block.TryGetGround(at, out Plane ground) || ground.Normal.Z < obj.MinSlope || ground.Normal.Z > obj.MaxSlope)
                        continue;

                    if (Math.Abs(ground.Normal.Z) > 0.0002f)
                        at.Z = -((ground.Normal.X * at.X) + (ground.Normal.Y * at.Y) + ground.D) / ground.Normal.Z;

                    Placement placement = new Placement(at, obj.BaseLoc.Orientation);
                    if (obj.Align)
                        placement = placement.Heading(HeadingOf(-ground.Normal));
                    else if (obj.MaxRotation > 0)
                        placement = placement.Heading(Noise(x, y, k, 63127u) * obj.MaxRotation);

                    ObjectShape shape = shapes(obj.ModelId);
                    if (shape == null || shape.IsEmpty || !WithinBlock(shape, placement))
                        continue;

                    block.Add(new Obstacle(shape, placement, Scale(obj, x, y, k)));
                }
            }
        }

        /// <summary>The client's per-object hash, 0 to 1, with a salt for what it decides.</summary>
        private static double Noise(uint x, uint y, uint k, uint salt)
            => unchecked((1813693831u * y) - ((k + salt) * ((1360117743u * y * x) + 1888038839u)) - (1109124029u * x)) * ToUnit;

        /// <summary>Where about its vertex an object stands: its base place, nudged, then turned a quarter at a time.</summary>
        private static Vector3 Displace(SceneryObject obj, uint x, uint y, uint k)
        {
            Vector3 origin = obj.BaseLoc.Origin;
            float dx = obj.DisplaceX <= 0 ? origin.X : (float)((Noise(x, y, k, 45773u) * obj.DisplaceX) + origin.X);
            float dy = obj.DisplaceY <= 0 ? origin.Y : (float)((Noise(x, y, k, 72719u) * obj.DisplaceY) + origin.Y);
            double quarter = unchecked((1813693831u * y) - (x * ((1870387557u * y) + 1109124029u)) - 402451965u) * ToUnit;
            if (quarter >= 0.75)
                return new Vector3(dy, -dx, origin.Z);
            if (quarter >= 0.5)
                return new Vector3(-dx, -dy, origin.Z);
            if (quarter >= 0.25)
                return new Vector3(-dy, dx, origin.Z);
            return new Vector3(dx, dy, origin.Z);
        }

        private static float Scale(SceneryObject obj, uint x, uint y, uint k)
        {
            if (obj.MinScale == obj.MaxScale)
                return obj.MaxScale;
            return (float)(Math.Pow(obj.MaxScale / obj.MinScale, Noise(x, y, k, 32593u)) * obj.MinScale);
        }

        /// <summary>The compass heading a direction points, ignoring its height.</summary>
        private static double HeadingOf(Vector3 v)
        {
            Vector2 flat = new Vector2(v.X, v.Y);
            if (flat.Length() < 0.0002f)
                return 0;
            return (450.0 - (Math.Atan2(flat.Y, flat.X) * 180.0 / Math.PI)) % 360.0;
        }

        /// <summary>
        /// Whether an object stands wholly within its landblock, as the client requires of scenery:
        /// by the sphere round it for one tested by its parts, by its cylinders or its spheres
        /// otherwise.
        /// </summary>
        private static bool WithinBlock(ObjectShape shape, Placement placement)
        {
            ObjectSetup setup = shape.Setup;
            if (setup == null || setup.UsesParts)
            {
                (Vector3 c, float r) = setup?.SortingSphere ?? (Vector3.Zero, 0f);
                Vector3 at = placement.ToOuter(c);
                return at.X >= r && at.Y >= r && at.X < 192f - r && at.Y < 192f - r;
            }

            if (setup.CylinderSpheres.Count > 0)
            {
                foreach (CylinderSphere cylinder in setup.CylinderSpheres)
                {
                    Vector3 at = placement.ToOuter(cylinder.Low);
                    if (at.X < cylinder.Radius || at.Y < cylinder.Radius || at.X >= 192f - cylinder.Radius || at.Y >= 192f - cylinder.Radius)
                        return false;
                }

                return true;
            }

            Vector3 centre = placement.ToOuter(setup.SortingSphere.Centre);
            float radius = setup.Spheres.Count > 0 ? setup.SortingSphere.Radius : 0f;
            return centre.X >= radius && centre.Y >= radius && centre.X < 192f - radius && centre.Y < 192f - radius;
        }

        /// <summary>Whether a point of the landblock is on a road, by the road bits of its landcell's corners and the region's road width.</summary>
        private static bool OnRoad(LandblockGeometry block, float width, float px, float py)
        {
            int x = (int)(px / Tile), y = (int)(py / Tile);
            uint Road(int i, int j) => (uint)(block.Terrain[(i * 9) + j] & 3);
            uint sw = Road(x, y), nw = Road(x, y + 1), se = Road(x + 1, y), ne = Road(x + 1, y + 1);
            if (sw == 0 && nw == 0 && se == 0 && ne == 0)
                return false;

            float dx = px - (x * Tile), dy = py - (y * Tile);
            float far = Tile - width;
            if (sw != 0)
            {
                if (nw != 0)
                {
                    if (se != 0)
                        return ne != 0 || dx < width || dy < width;
                    if (ne != 0)
                        return dx < width || dy > far;
                    return dx < width;
                }

                if (se != 0)
                    return ne != 0 ? dx > far || dy < width : dy < width;
                if (ne != 0)
                    return Math.Abs(dx - dy) < width;
                return dx + dy < width;
            }

            if (nw != 0)
            {
                if (se != 0)
                    return ne != 0 ? dx > far || dy > far : Math.Abs(dx + dy - Tile) < width;
                if (ne != 0)
                    return dy > far;
                return Tile + dx - dy < width;
            }

            if (se != 0)
                return ne != 0 ? dx > far : Tile - dx + dy < width;
            return Tile * 2f - dx - dy < width;
        }
    }
}
