using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace AC.Dat.Geometry
{
    /// <summary>
    /// A landblock's ground (file 0xXXYYFFFF of the cell archive): the terrain word and the height
    /// of each of its nine by nine vertices, 24 metres apart, numbered east first then north.
    /// </summary>
    public sealed class LandblockGround
    {
        private LandblockGround(uint id, ushort[] terrain, byte[] heights)
        {
            Id = id;
            Terrain = terrain;
            Heights = heights;
        }

        public uint Id { get; }

        /// <summary>Each vertex's terrain word: road in the low two bits, the terrain type in the next five, scenery in the top five.</summary>
        public IReadOnlyList<ushort> Terrain { get; }

        /// <summary>Each vertex's height, an index into the region's height table.</summary>
        public IReadOnlyList<byte> Heights { get; }

        /// <summary>The id, a flag, the 81 terrain words, then the 81 heights. Null when the bytes do not read.</summary>
        public static LandblockGround Parse(byte[] data)
        {
            if (data == null)
                return null;

            try
            {
                ModelReader reader = new ModelReader(data);
                uint id = reader.ReadUInt32();
                reader.ReadUInt32();
                ushort[] terrain = new ushort[81];
                for (int i = 0; i < 81; i++)
                    terrain[i] = reader.ReadUInt16();
                byte[] heights = new byte[81];
                for (int i = 0; i < 81; i++)
                    heights[i] = reader.ReadByte();
                return new LandblockGround(id, terrain, heights);
            }
            catch (InvalidDataException)
            {
                return null;
            }
        }
    }

    /// <summary>A building of a landblock: its shell's model, where it stands, and the cells its doorways lead into.</summary>
    public sealed class BuildingInfo
    {
        public BuildingInfo(uint modelId, Placement placement, IReadOnlyList<uint> doorwayCells)
        {
            ModelId = modelId;
            Placement = placement;
            DoorwayCells = doorwayCells ?? Array.Empty<uint>();
        }

        public uint ModelId { get; }

        public Placement Placement { get; }

        /// <summary>The indoor cells the building's doorways lead into, whole ids.</summary>
        public IReadOnlyList<uint> DoorwayCells { get; }
    }

    /// <summary>
    /// What stands on a landblock (file 0xXXYYFFFE of the cell archive): how many indoor cells it
    /// has, the static objects the client places - fences, rocks, wells - and the buildings.
    /// </summary>
    public sealed class LandblockLayout
    {
        private LandblockLayout(uint id, int cellCount, IReadOnlyList<StaticObject> objects, IReadOnlyList<BuildingInfo> buildings)
        {
            Id = id;
            CellCount = cellCount;
            Objects = objects;
            Buildings = buildings;
        }

        public uint Id { get; }

        /// <summary>The landblock's indoor cells are 0x0100 up to this many on.</summary>
        public int CellCount { get; }

        public IReadOnlyList<StaticObject> Objects { get; }

        public IReadOnlyList<BuildingInfo> Buildings { get; }

        /// <summary>
        /// The id, the count of indoor cells, the objects (a count, each a model and frame), the
        /// count of buildings and a mask, then each building: its model, frame, leaf count and
        /// doorways (a count, each its flags, the cell beyond, that cell's doorway and the cells
        /// seen through it, padded to a word). The restrictions after them are not needed.
        /// </summary>
        public static LandblockLayout Parse(byte[] data)
        {
            if (data == null)
                return null;

            try
            {
                ModelReader reader = new ModelReader(data);
                uint id = reader.ReadUInt32();
                int cells = reader.ReadCount(1 << 16);

                int count = reader.ReadCount(1 << 16);
                StaticObject[] objects = new StaticObject[count];
                for (int i = 0; i < count; i++)
                    objects[i] = new StaticObject(reader.ReadUInt32(), reader.ReadPlacement());

                int buildings = reader.ReadUInt16();
                reader.ReadUInt16();
                uint landblock = id & 0xFFFF0000u;
                BuildingInfo[] built = new BuildingInfo[buildings];
                for (int b = 0; b < buildings; b++)
                {
                    uint model = reader.ReadUInt32();
                    Placement placement = reader.ReadPlacement();
                    reader.ReadUInt32(); // leaves
                    int doorways = reader.ReadCount(1024);
                    List<uint> beyond = new List<uint>(doorways);
                    for (int d = 0; d < doorways; d++)
                    {
                        reader.ReadUInt16(); // flags
                        uint other = landblock | reader.ReadUInt16();
                        reader.ReadUInt16(); // its doorway
                        reader.Skip(reader.ReadUInt16() * 2);
                        reader.Align();
                        if (!beyond.Contains(other))
                            beyond.Add(other);
                    }

                    built[b] = new BuildingInfo(model, placement, beyond);
                }

                return new LandblockLayout(id, cells, objects, built);
            }
            catch (InvalidDataException)
            {
                return null;
            }
        }
    }

    /// <summary>One kind of object a scene may scatter on the ground, and how it is scattered.</summary>
    public sealed class SceneryObject
    {
        public uint ModelId;
        public Placement BaseLoc;
        public float Frequency;
        public float DisplaceX;
        public float DisplaceY;
        public float MinScale;
        public float MaxScale;
        public float MaxRotation;
        public float MinSlope;
        public float MaxSlope;
        public bool Align;
        public uint WeenieObj;
    }

    /// <summary>
    /// The region's description (file 0x13000000 of the portal archive), as far as the ground goes:
    /// the heights the vertices index, the width of roads, and which scenes each terrain type
    /// scatters.
    /// </summary>
    public sealed class RegionInfo
    {
        public const uint FileId = 0x13000000;

        private RegionInfo()
        {
        }

        /// <summary>The height, in metres, of each of a vertex's 256 height steps.</summary>
        public IReadOnlyList<float> LandHeights { get; private set; } = Array.Empty<float>();

        public float RoadWidth { get; private set; }

        /// <summary>For each terrain type, its scene types, by a vertex's scenery number.</summary>
        public IReadOnlyList<uint[]> TerrainSceneTypes { get; private set; } = Array.Empty<uint[]>();

        /// <summary>For each scene type, its scenes (files 0x12......).</summary>
        public IReadOnlyList<uint[]> SceneTypeScenes { get; private set; } = Array.Empty<uint[]>();

        /// <summary>
        /// Reads the region: its numbers and name, the land's measures and heights, the game's
        /// calendar, then - by the parts mask - the sky, the sounds and the scene types, and the
        /// terrain types with their scene types. What follows them is not needed. Null when the
        /// bytes do not read.
        /// </summary>
        public static RegionInfo Parse(byte[] data)
        {
            if (data == null)
                return null;

            try
            {
                ModelReader reader = new ModelReader(data);
                RegionInfo region = new RegionInfo();
                reader.Skip(12);
                reader.SkipString();
                reader.Align();

                reader.Skip(4 + 4 + 4 + 4 + 4 + 4 + 4);
                region.RoadWidth = reader.ReadSingle();
                float[] heights = new float[256];
                for (int i = 0; i < 256; i++)
                    heights[i] = reader.ReadSingle();
                region.LandHeights = heights;

                SkipCalendar(reader);

                uint parts = reader.ReadUInt32();
                if ((parts & 0x10) != 0)
                    SkipSky(reader);
                if ((parts & 0x1) != 0)
                    SkipSounds(reader);

                List<uint[]> sceneTypes = new List<uint[]>();
                if ((parts & 0x2) != 0)
                {
                    int count = reader.ReadCount(1 << 16);
                    for (int i = 0; i < count; i++)
                    {
                        reader.ReadUInt32();
                        sceneTypes.Add(ReadWords(reader));
                    }
                }

                region.SceneTypeScenes = sceneTypes;

                List<uint[]> terrainTypes = new List<uint[]>();
                int terrains = reader.ReadCount(1 << 10);
                for (int i = 0; i < terrains; i++)
                {
                    reader.SkipString();
                    reader.Align();
                    reader.ReadUInt32();
                    terrainTypes.Add(ReadWords(reader));
                }

                region.TerrainSceneTypes = terrainTypes;
                return region;
            }
            catch (InvalidDataException)
            {
                return null;
            }
        }

        private static uint[] ReadWords(ModelReader reader)
        {
            int count = reader.ReadCount(1 << 16);
            uint[] words = new uint[count];
            for (int i = 0; i < count; i++)
                words[i] = reader.ReadUInt32();
            return words;
        }

        private static void SkipNamed(ModelReader reader, int before)
        {
            reader.Skip(before);
            reader.SkipString();
            reader.Align();
        }

        /// <summary>The calendar: its start and lengths, the year's name, the times of day, the weekdays and the seasons.</summary>
        private static void SkipCalendar(ModelReader reader)
        {
            SkipNamed(reader, 8 + 4 + 4 + 4);
            int times = reader.ReadCount(1024);
            for (int i = 0; i < times; i++)
                SkipNamed(reader, 4 + 4);
            int days = reader.ReadCount(1024);
            for (int i = 0; i < days; i++)
                SkipNamed(reader, 0);
            int seasons = reader.ReadCount(1024);
            for (int i = 0; i < seasons; i++)
                SkipNamed(reader, 4);
        }

        /// <summary>The sky: its tick sizes, then each day group with its sky objects and times of day.</summary>
        private static void SkipSky(ModelReader reader)
        {
            reader.Skip(16);
            reader.Align();
            int groups = reader.ReadCount(1024);
            for (int g = 0; g < groups; g++)
            {
                SkipNamed(reader, 4);
                int objects = reader.ReadCount(1 << 16);
                reader.Skip(objects * 36);
                int times = reader.ReadCount(1 << 16);
                for (int t = 0; t < times; t++)
                {
                    reader.Skip(44);
                    reader.Align();
                    int replaced = reader.ReadCount(1 << 16);
                    reader.Skip(replaced * 24);
                }
            }
        }

        /// <summary>The sounds: each ambient table with its sounds.</summary>
        private static void SkipSounds(ModelReader reader)
        {
            int tables = reader.ReadCount(1 << 16);
            for (int i = 0; i < tables; i++)
            {
                reader.ReadUInt32();
                int sounds = reader.ReadCount(1 << 16);
                reader.Skip(sounds * 20);
            }
        }
    }

    /// <summary>A scene (file 0x12......): the objects it may scatter round a vertex.</summary>
    public static class SceneFile
    {
        /// <summary>The id, then the objects: each its model, base frame and how it is scattered. Null when the bytes do not read.</summary>
        public static IReadOnlyList<SceneryObject> Parse(byte[] data)
        {
            if (data == null)
                return null;

            try
            {
                ModelReader reader = new ModelReader(data);
                reader.ReadUInt32();
                int count = reader.ReadCount(1 << 16);
                SceneryObject[] objects = new SceneryObject[count];
                for (int i = 0; i < count; i++)
                {
                    objects[i] = new SceneryObject
                    {
                        ModelId = reader.ReadUInt32(),
                        BaseLoc = reader.ReadPlacement(),
                        Frequency = reader.ReadSingle(),
                        DisplaceX = reader.ReadSingle(),
                        DisplaceY = reader.ReadSingle(),
                        MinScale = reader.ReadSingle(),
                        MaxScale = reader.ReadSingle(),
                        MaxRotation = reader.ReadSingle(),
                        MinSlope = reader.ReadSingle(),
                        MaxSlope = reader.ReadSingle(),
                        Align = reader.ReadUInt32() != 0,
                    };
                    reader.ReadUInt32(); // orientation
                    objects[i].WeenieObj = reader.ReadUInt32();
                }

                return objects;
            }
            catch (InvalidDataException)
            {
                return null;
            }
        }
    }
}
