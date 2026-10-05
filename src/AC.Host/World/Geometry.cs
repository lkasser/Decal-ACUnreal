using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using AC.Dat.Geometry;

namespace AC.Host.World
{
    /// <summary>
    /// The world's solid geometry for plugins: the ground, buildings, walls, static objects and
    /// scenery, read from the client's archives - what a Decal plugin read out of the client's own
    /// memory to tell whether a spell or an arrow would reach its target.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For the game thread, and never waits on the archives there: what is not read yet is read on
    /// a worker, and until it is a question about it answers null - "cannot say yet" - which a
    /// caller takes as it took no geometry at all.
    /// </para>
    /// <para>
    /// Positions are a <see cref="Location"/>'s: a cell and a point in its landblock. Offsets are in
    /// metres, east, north and up.
    /// </para>
    /// </remarks>
    public interface IWorldGeometry
    {
        /// <summary>
        /// An object's height at its size: its setup's height times its scale, as the game reckons
        /// what it aims at. Null for an object without a setup, or one not read yet.
        /// </summary>
        float? HeightOf(WorldObject obj);

        /// <summary>
        /// Sends a sphere of <paramref name="radius"/> metres from <paramref name="from"/> through each
        /// of <paramref name="moves"/> in turn, following it from cell to cell, and tests it after
        /// each move against the ground, the walls, the buildings, the client's own objects and
        /// those of the server's objects <paramref name="counts"/> says yes to. The first
        /// <paramref name="movesInFlight"/> tests are short of the target, where coming within the
        /// radius of the ground is a touch; after them only going under it is. Answers the first
        /// contact, or null when the places or shapes it needs are still being read.
        /// </summary>
        SweepContact? Sweep(Location from, IReadOnlyList<Vector3> moves, int movesInFlight, float radius, Func<WorldObject, bool> counts);
    }

    /// <summary>
    /// <see cref="IWorldGeometry"/> over the client's portal and cell archives, its landblocks and
    /// shapes read on workers as they are first needed and kept.
    /// </summary>
    public sealed class ArchiveGeometry : IWorldGeometry
    {
        /// <summary>How far from a sweep's track a server object is looked at all.</summary>
        private const float ObjectMargin = 20f;

        private readonly WorldGeometry _world;
        private readonly IWorldView _objects;
        private readonly ConcurrentDictionary<ulong, Task> _reading = new ConcurrentDictionary<ulong, Task>();

        public ArchiveGeometry(WorldGeometry world, IWorldView objects)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _objects = objects;
        }

        /// <summary>The geometry itself, for reading ahead or for tests that wait for it.</summary>
        public WorldGeometry World => _world;

        /// <summary>Raised on a worker when reading a landblock or a shape failed.</summary>
        public event EventHandler<Exception> ReadFailed;

        public float? HeightOf(WorldObject obj)
        {
            if (obj == null || obj.SetupId == 0)
                return null;
            uint setup = obj.SetupId;
            if (!_world.HasShape(setup))
            {
                ReadLater(Shapes | setup, () => _world.Shape(setup));
                return null;
            }

            ObjectShape shape = _world.Shape(setup);
            return shape == null ? (float?)null : shape.Height * (obj.Scale ?? 1f);
        }

        public SweepContact? Sweep(Location from, IReadOnlyList<Vector3> moves, int movesInFlight, float radius, Func<WorldObject, bool> counts)
        {
            if (moves == null)
                throw new ArgumentNullException(nameof(moves));

            CellPoint start = new CellPoint(from.LandblockCell, new Vector3(from.X, from.Y, from.Z));

            // The landblocks the track crosses, and the box round it.
            Vector3 flat = start.Flat, low = flat, high = flat;
            uint last = start.Cell & 0xFFFF0000u;
            bool ready = Ready(last);
            foreach (Vector3 move in moves)
            {
                flat += move;
                low = Vector3.Min(low, flat);
                high = Vector3.Max(high, flat);
                if (flat.X < 0 || flat.Y < 0 || flat.X >= 255 * 192f || flat.Y >= 255 * 192f)
                    continue;

                uint landblock = ((uint)(flat.X / 192f) << 24) | ((uint)(flat.Y / 192f) << 16);
                if (landblock != last)
                {
                    last = landblock;
                    ready &= Ready(landblock);
                }
            }

            List<Obstacle> objects = Objects(low, high, radius, counts, ref ready);
            if (!ready)
                return null;

            return _world.Sweep(start, moves, movesInFlight, radius, objects);
        }

        /// <summary>
        /// The server's objects to test, placed in the world's flat frame: those
        /// <paramref name="counts"/> says yes to near the track. One whose shape is not read yet
        /// makes the sweep wait.
        /// </summary>
        private List<Obstacle> Objects(Vector3 low, Vector3 high, float radius, Func<WorldObject, bool> counts, ref bool ready)
        {
            List<Obstacle> objects = new List<Obstacle>();
            if (counts == null || _objects == null)
                return objects;

            float margin = ObjectMargin + radius;
            foreach (WorldObject obj in _objects.Objects)
            {
                if (!(obj.Location is Location at) || obj.SetupId == 0)
                    continue;

                Vector3 flat = new Vector3(((at.LandblockCell >> 24) * 192f) + at.X, (((at.LandblockCell >> 16) & 0xFF) * 192f) + at.Y, at.Z);
                if (flat.X < low.X - margin || flat.Y < low.Y - margin || flat.X > high.X + margin || flat.Y > high.Y + margin
                    || flat.Z < low.Z - margin || flat.Z > high.Z + margin || !counts(obj))
                    continue;

                if (!_world.HasShape(obj.SetupId))
                {
                    uint setup = obj.SetupId;
                    ReadLater(Shapes | setup, () => _world.Shape(setup));
                    ready = false;
                    continue;
                }

                ObjectShape shape = _world.Shape(obj.SetupId);
                if (shape == null || shape.IsEmpty)
                    continue;

                Quaternion turn = new Quaternion(at.QX, at.QY, at.QZ, at.QW);
                objects.Add(new Obstacle(shape, new Placement(flat, turn), obj.Scale ?? 1f, obj.Id));
            }

            return objects;
        }

        /// <summary>Whether a landblock has been read; if not, has it and those round it read on a worker.</summary>
        private bool Ready(uint cell)
        {
            uint landblock = cell & 0xFFFF0000u;
            if (_world.IsLoaded(landblock))
                return true;

            uint x = landblock >> 24, y = (landblock >> 16) & 0xFF;
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    long nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx > 254 || ny > 254)
                        continue;

                    uint neighbour = (uint)((nx << 24) | (ny << 16));
                    if (!_world.IsLoaded(neighbour))
                        ReadLater(neighbour, () => _world.Load(neighbour));
                }
            }

            return false;
        }

        /// <summary>Marks a reading key as a shape's, so it never meets a landblock's.</summary>
        private const ulong Shapes = 1UL << 32;

        /// <summary>Reads something once on a worker; a failure is reported and not tried again.</summary>
        private void ReadLater(ulong key, Action read)
        {
            _reading.GetOrAdd(key, _ => Task.Run(() =>
            {
                try
                {
                    read();
                }
                catch (Exception ex)
                {
                    ReadFailed?.Invoke(this, ex);
                }
            }));
        }

        /// <summary>Waits for everything asked for so far to have been read: for tests and tools, never the game thread.</summary>
        public void WaitForReading()
        {
            foreach (Task task in _reading.Values)
                task.Wait();
        }
    }
}
