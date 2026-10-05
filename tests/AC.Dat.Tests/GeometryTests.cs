using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using AC.Dat;
using AC.Dat.Geometry;
using Xunit;

namespace AC.Dat.Tests
{
    /// <summary>
    /// The world's solid geometry, read from the client's archives: the ground, buildings, fences,
    /// scenery and indoor cells, and a sphere sent through them as Virindi Tank's wall check sent
    /// one. The places are real ones - Yaraq's paddock (landblock 0x2B11), where the recorded fights
    /// happen, and a small dungeon (0x01D9) - so these read the real files and skip on a machine
    /// without them; the shapes' own tests need no files.
    /// </summary>
    public class GeometryTests
    {
        private static readonly string PortalPath = PortalData.FindPortalDat();

        private static readonly string CellPath = CellData.FindBeside(PortalPath);

        private const uint Paddock = 0x2B110000, Dungeon = 0x01D90000;

        /// <summary>The fence posts and rails round the paddock: setup 0x020003B5.</summary>
        private const uint Fence = 0x020003B5;

        /// <summary>The double barn on the paddock's west side, a building at x 90 and one at x 102.</summary>
        private const uint Barn = 0x0100081C;

        private static readonly Lazy<(PortalData Portal, CellData Cells, WorldGeometry World)> Archives =
            new Lazy<(PortalData, CellData, WorldGeometry)>(() =>
            {
                PortalData portal = PortalData.Open(PortalPath);
                CellData cells = CellData.Open(CellPath);
                return (portal, cells, WorldGeometry.Open(portal, cells));
            });

        private static WorldGeometry World()
        {
            Skip.If(PortalPath == null || CellPath == null, "No client_portal.dat and client_cell_1.dat on this machine.");
            return Archives.Value.World;
        }

        /// <summary>A straight sweep from one point to another of a cell's landblock, in steps of 0.7 m, with a sphere of 0.4 m.</summary>
        private static SweepContact Line(WorldGeometry world, uint cell, Vector3 from, Vector3 to, IReadOnlyList<Obstacle> objects = null)
        {
            Vector3 d = to - from;
            int steps = (int)Math.Ceiling(d.Length() / 0.7f);
            List<Vector3> moves = new List<Vector3> { Vector3.Zero };
            for (int i = 0; i < steps; i++)
                moves.Add(d / steps);
            return world.Sweep(new CellPoint(cell, from), moves, moves.Count, 0.4f, objects ?? Array.Empty<Obstacle>());
        }

        // ------------------------------------------------------------------- the shapes, without files

        [Fact]
        public void ASphereTouchesAPolygonOverItsFaceOrByAnEdgeAndNotBeyond()
        {
            SolidPolygon square = new SolidPolygon(new[] { new Vector3(0, 0, 0), new Vector3(2, 0, 0), new Vector3(2, 2, 0), new Vector3(0, 2, 0) });

            Assert.Equal(1f, square.Plane.Normal.Z, 4);
            Assert.True(square.Touches(new Vector3(1, 1, 0.3f), 0.4f));
            Assert.True(square.Touches(new Vector3(1, 1, -0.3f), 0.4f));
            Assert.False(square.Touches(new Vector3(1, 1, 0.5f), 0.4f));
            Assert.True(square.Touches(new Vector3(2.3f, 1, 0), 0.4f));
            Assert.False(square.Touches(new Vector3(2.5f, 1, 0), 0.4f));
            Assert.True(square.Touches(new Vector3(2.2f, 2.2f, 0), 0.4f));
            Assert.False(square.Touches(new Vector3(2.3f, 2.3f, 0), 0.4f));
        }

        [Fact]
        public void AnObjectsCylinderIsHitFromItsFootToItsTopAndNoWiderThanItIs()
        {
            byte[] setup = SetupWithOneCylinder(radius: 0.5f, height: 1.8f);
            ObjectShape shape = ObjectShape.Of(ObjectSetup.Parse(setup), _ => null);
            Assert.NotNull(shape);
            Assert.Equal(1.8f, shape.Height);

            Placement at = new Placement(new Vector3(10, 10, 0), Quaternion.Identity);
            Assert.True(shape.Touches(at, 1f, new Vector3(10.8f, 10, 0.9f), 0.4f));
            Assert.False(shape.Touches(at, 1f, new Vector3(11f, 10, 0.9f), 0.4f));
            Assert.True(shape.Touches(at, 1f, new Vector3(10, 10, 2.1f), 0.4f));
            Assert.False(shape.Touches(at, 1f, new Vector3(10, 10, 2.3f), 0.4f));

            // Twice the size, twice the reach.
            Assert.True(shape.Touches(at, 2f, new Vector3(11.3f, 10, 0.9f), 0.4f));
        }

        /// <summary>A Setup of no parts and one cylinder, as the file lays one out.</summary>
        private static byte[] SetupWithOneCylinder(float radius, float height)
        {
            List<byte> bytes = new List<byte>();
            void Word(uint w) => bytes.AddRange(BitConverter.GetBytes(w));
            void Float(float f) => bytes.AddRange(BitConverter.GetBytes(f));

            Word(0x02000001); // id
            Word(0);          // flags
            Word(0);          // parts
            Word(0);          // holding locations
            Word(0);          // connection points
            Word(0);          // placements
            Word(1);          // cylinders
            Float(0); Float(0); Float(0); Float(radius); Float(height);
            Word(0);          // spheres
            Float(height); Float(radius); Float(0); Float(0);
            Float(0); Float(0); Float(height / 2); Float(height);
            Float(0); Float(0); Float(height / 2); Float(height);
            Word(0);          // lights
            for (int i = 0; i < 5; i++)
                Word(0);
            return bytes.ToArray();
        }

        [Fact]
        public void AnIndoorCellReadsWithItsRoomShapeDoorwaysAndPlace()
        {
            const string hex =
                "2E01122B010000002E01122B09020D00C9011D071B071A07BB041C071A0719071F07AD03000000000443000070420000404231BD3BB30000000000000000000080BF"
                + "010017003A01000001001600370101002F0130013101320133013401350136013701380139013A013B01";
            EnvCellInfo room = EnvCellInfo.Parse(Convert.FromHexString(hex));

            Assert.Equal(0x0D0003ADu, room.EnvironmentId);
            Assert.Equal(0, room.RoomIndex);
            Assert.Equal(new Vector3(132, 60, 48), room.Placement.Origin);
            Assert.Equal(-1f, room.Placement.Orientation.Z, 4);
            Assert.Equal(new[] { 0x2B12013Au, 0x2B120137u }, room.Doorways.Select(d => d.OtherCellId));
            Assert.Equal(new ushort[] { 23, 22 }, room.Doorways.Select(d => d.PolygonId));
            Assert.Empty(room.StaticObjects);
        }

        // ------------------------------------------------------------------- reading the archives

        [SkippableFact]
        public void TheRegionGivesTheLandsHeightsRoadsAndScenes()
        {
            RegionInfo region = World().Region;

            Assert.NotNull(region);
            Assert.Equal(256, region.LandHeights.Count);
            Assert.True(region.LandHeights[255] > region.LandHeights[0]);
            Assert.True(region.RoadWidth > 0);
            Assert.Equal(32, region.TerrainSceneTypes.Count);
            Assert.NotEmpty(region.SceneTypeScenes);
        }

        /// <summary>
        /// The paddock as the archives have it: level ground at 48 m, its fence, the double barn
        /// with the cells its doorways open into, and the landscape's own scenery about it.
        /// </summary>
        [SkippableFact]
        public void ThePaddocksLandblockHasItsGroundFenceBarnAndCells()
        {
            WorldGeometry world = World();
            LandblockGeometry block = world.Load(Paddock);

            Assert.NotNull(block);
            Assert.Equal(48f, block.GroundHeight(new Vector3(125, 115, 0)).Value, 2);
            Assert.True(block.Outdoors.Count(o => o.Shape.ModelId == Fence) >= 40);

            Obstacle[] barns = block.Outdoors.Where(o => o.IsBuilding && o.Shape.ModelId == Barn).ToArray();
            Assert.Equal(2, barns.Length);
            Obstacle east = barns.Single(b => Math.Abs(b.Placement.Origin.X - 102) < 0.1f);
            Assert.Equal(4, east.DoorwayCells.Count);
            Assert.All(east.DoorwayCells, cell => Assert.NotNull(world.Indoor(cell)));

            // The barn's east doorway, and the floor of the room it opens into.
            IndoorCell doorway = world.Indoor(0x2B110110);
            Assert.True(doorway.Contains(new Vector3(108, 125.9f, 49)));
            Assert.False(doorway.Contains(new Vector3(102, 125.9f, 49)));

            Assert.Contains(block.Outdoors, o => !o.IsBuilding && o.Shape.ModelId >> 24 == 0x02 && o.Scale != 1f);
        }

        // ------------------------------------------------------------------- sweeping

        /// <summary>Across the open paddock nothing is in the way: a bolt from one cow to another reaches.</summary>
        [SkippableFact]
        public void AcrossTheOpenPaddockTheWayIsClear()
        {
            WorldGeometry world = World();
            world.Load(Paddock);

            SweepContact contact = Line(world, Paddock | 0x2D, new Vector3(120, 105, 49.2f), new Vector3(135, 128, 48.8f));

            Assert.Equal(ContactKind.None, contact.Kind);
        }

        /// <summary>
        /// West along y = 123 from inside the paddock the barn's east wall stops it, about x = 108,
        /// well short of the fence beyond.
        /// </summary>
        [SkippableFact]
        public void TheBarnsWallStopsIt()
        {
            WorldGeometry world = World();
            world.Load(Paddock);
            Vector3 from = new Vector3(125, 123, 49.2f), to = new Vector3(75, 123, 48.8f);

            SweepContact contact = Line(world, Paddock | 0x2D, from, to);

            Assert.Equal(ContactKind.Static, contact.Kind);
            float x = from.X + ((to.X - from.X) * contact.Step / (float)Math.Ceiling((to - from).Length() / 0.7f));
            Assert.InRange(x, 106f, 110f);
        }

        /// <summary>The same line nudged into the barn's east doorway goes in, and on through the barn's room.</summary>
        [SkippableFact]
        public void ThroughTheBarnsDoorwayItGoesIn()
        {
            WorldGeometry world = World();
            world.Load(Paddock);

            SweepContact contact = Line(world, Paddock | 0x2D, new Vector3(118, 125.9f, 49f), new Vector3(104, 125.9f, 49f));

            Assert.Equal(ContactKind.None, contact.Kind);
        }

        /// <summary>The fence along x = 115.5 stops a shot at a cow's height, and the ground one aimed below it.</summary>
        [SkippableFact]
        public void TheFenceAndTheGroundStopIt()
        {
            WorldGeometry world = World();
            world.Load(Paddock);

            SweepContact fence = Line(world, Paddock | 0x2D, new Vector3(125, 110, 49.2f), new Vector3(105, 110, 48.8f));
            Assert.Equal(ContactKind.Static, fence.Kind);
            Assert.InRange(fence.Step, 12, 15);

            SweepContact ground = Line(world, Paddock | 0x2D, new Vector3(125, 110, 49.2f), new Vector3(135, 120, 44f));
            Assert.Equal(ContactKind.Ground, ground.Kind);
        }

        /// <summary>
        /// A server object in the way is met where it stands, and named: the sphere reaches the cow
        /// before anything else.
        /// </summary>
        [SkippableFact]
        public void AServerObjectInTheWayIsMetAndNamed()
        {
            WorldGeometry world = World();
            world.Load(Paddock);
            ObjectShape cow = ObjectShape.Of(ObjectSetup.Parse(SetupWithOneCylinder(radius: 0.8f, height: 1.6f)), _ => null);
            Vector3 flat = new Vector3((0x2B * 192f) + 130, (0x11 * 192f) + 120, 48);
            Obstacle standing = new Obstacle(cow, new Placement(flat, Quaternion.Identity), 1f, 0x80001234);

            SweepContact contact = Line(world, Paddock | 0x2D, new Vector3(120, 115, 49.2f), new Vector3(130, 120, 48.8f), new[] { standing });

            Assert.Equal(ContactKind.Object, contact.Kind);
            Assert.Equal(0x80001234u, contact.ObjectId);
        }

        /// <summary>
        /// In a dungeon: across one room, and on through the open way into the next, is clear; a
        /// line through the rock to a room beyond meets a wall, and so does one into the floor.
        /// </summary>
        [SkippableFact]
        public void InADungeonTheWallsAndFloorStopItAndTheRoomsAreOpen()
        {
            WorldGeometry world = World();
            LandblockGeometry block = world.Load(Dungeon);
            Assert.True(block.Cells.Count >= 30);

            Assert.Equal(ContactKind.None, Line(world, Dungeon | 0x100, new Vector3(-3, -52, 1.5f), new Vector3(3, -48, 1.5f)).Kind);
            Assert.Equal(ContactKind.None, Line(world, Dungeon | 0x100, new Vector3(-2, -50, 1.5f), new Vector3(12, -50, 1.5f)).Kind);
            Assert.Equal(ContactKind.Wall, Line(world, Dungeon | 0x100, new Vector3(0, -50, 1.5f), new Vector3(20, -20, 1.5f)).Kind);
            Assert.Equal(ContactKind.Wall, Line(world, Dungeon | 0x100, new Vector3(0, -50, 1.5f), new Vector3(3, -50, -2f)).Kind);
        }

        /// <summary>A landblock is read in tens of milliseconds, and once read a sweep takes microseconds.</summary>
        [SkippableFact]
        public void ReadingIsQuickAndSweepingQuicker()
        {
            WorldGeometry world = World();
            Stopwatch watch = Stopwatch.StartNew();
            world.Load(0x2B120000);
            Assert.InRange(watch.ElapsedMilliseconds, 0, 2000);

            world.Load(Paddock);
            for (int i = 0; i < 200; i++)
                Line(world, Paddock | 0x2D, new Vector3(120, 105, 49.2f), new Vector3(135, 128, 48.8f));

            watch.Restart();
            for (int i = 0; i < 200; i++)
                Line(world, Paddock | 0x2D, new Vector3(120, 105, 49.2f), new Vector3(135, 128, 48.8f));
            Assert.InRange(watch.Elapsed.TotalMilliseconds / 200, 0, 2.0);
        }
    }
}
