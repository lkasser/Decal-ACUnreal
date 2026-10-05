using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using AC.Host.World;
using Decal.Adapter.Hosting;
using HostObject = AC.Host.World.WorldObject;

namespace Decal.Adapter.Wrappers
{
    /// <summary>
    /// Decal's world filter: every object the client knows of, and the events for objects
    /// coming, changing and going. Answered from the host's world model.
    /// </summary>
    public class WorldFilter : DisposableByRefObject
    {
        private readonly DecalRuntime _runtime;

        internal WorldFilter(DecalRuntime runtime)
        {
            _runtime = runtime;
        }

        /// <summary>The object with this id, or null if the host has not seen it.</summary>
        public WorldObject this[int guid]
            => _runtime.Host.World.TryGet(unchecked((uint)guid), out HostObject obj) ? new WorldObject(obj) : null;

        /// <summary>The vendor whose window is open. The host does not decode vendor windows, so none.</summary>
        public Vendor OpenVendor => null;

        /// <summary>
        /// The distance between two objects in map units - 240 world units each - which is
        /// why plugins multiply it by 240 to get metres. Zero when either has no position.
        /// </summary>
        public double Distance(int id1, int id2) => Distance(id1, id2, false);

        public double Distance(int id1, int id2, bool use3d)
        {
            Location? a = PositionOf(id1);
            Location? b = PositionOf(id2);
            if (!a.HasValue || !b.HasValue)
                return 0;

            double dx = Global(a.Value, true) - Global(b.Value, true);
            double dy = Global(a.Value, false) - Global(b.Value, false);
            double dz = use3d ? a.Value.Z - b.Value.Z : 0;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz) / 240.0;
        }

        public WorldObjectCollection GetAll() => Collect(new ByAllFilter());

        /// <summary>Everything the character carries or wears, however deep in packs.</summary>
        public WorldObjectCollection GetInventory() => Collect(new ByInventoryFilter());

        /// <summary>Everything lying in the world rather than inside something.</summary>
        public WorldObjectCollection GetLandscape() => Collect(new ByLandscapeFilter());

        public WorldObjectCollection GetByContainer(int container) => Collect(new ByContainerFilter(container));

        public WorldObjectCollection GetByCategory(int category) => Collect(new ByCategoryFilter(category));

        public WorldObjectCollection GetByName(string name) => Collect(new ByNameFilter(name));

        public WorldObjectCollection GetByNameSubstring(string name) => Collect(new ByNameSubStringFilter { Name = name });

        public WorldObjectCollection GetByObjectClass(ObjectClass objClass) => Collect(new ByObjectClassFilter(objClass));

        public WorldObjectCollection GetByOwner(int owner) => Collect(new ByOwnerFilter(owner));

        public event EventHandler<CreateObjectEventArgs> CreateObject;

        public event EventHandler<ChangeObjectEventArgs> ChangeObject;

        public event EventHandler<MoveObjectEventArgs> MoveObject;

        public event EventHandler<ReleaseObjectEventArgs> ReleaseObject;

        // Trade and vendor windows: the host does not decode them, so these never fire, but
        // plugins subscribe to them at startup and must be able to.
#pragma warning disable CS0067
        public event EventHandler<AcceptTradeEventArgs> AcceptTrade;

        public event EventHandler<AddTradeItemEventArgs> AddTradeItem;

        public event EventHandler<ApproachVendorEventArgs> ApproachVendor;

        public event EventHandler<DeclineTradeEventArgs> DeclineTrade;

        public event EventHandler<EndTradeEventArgs> EndTrade;

        public event EventHandler<EnterTradeEventArgs> EnterTrade;

        public event EventHandler<FailToAddTradeItemEventArgs> FailToAddTradeItem;

        public event EventHandler FailToCompleteTrade;

        public event EventHandler ReleaseDone;

        public event EventHandler<ResetTradeEventArgs> ResetTrade;
#pragma warning restore CS0067

        internal DecalRuntime Runtime => _runtime;

        internal void OnCreateObject(HostObject obj)
            => _runtime.Raise(CreateObject, this, new CreateObjectEventArgs(new WorldObject(obj)), nameof(CreateObject));

        internal void OnChangeObject(HostObject obj, WorldChangeType change)
            => _runtime.Raise(ChangeObject, this, new ChangeObjectEventArgs(new WorldObject(obj), change), nameof(ChangeObject));

        internal void OnMoveObject(HostObject obj)
            => _runtime.Raise(MoveObject, this, new MoveObjectEventArgs(new WorldObject(obj)), nameof(MoveObject));

        internal void OnReleaseObject(HostObject obj)
            => _runtime.Raise(ReleaseObject, this, new ReleaseObjectEventArgs(new WorldObject(obj)), nameof(ReleaseObject));

        private WorldObjectCollection Collect(WorldObjectCollectionFilter filter) => new WorldObjectCollection(this, filter);

        private Location? PositionOf(int id)
        {
            uint uid = unchecked((uint)id);
            if (uid == _runtime.Host.Character.Id && _runtime.Host.Character.Location.HasValue)
                return _runtime.Host.Character.Location;

            return _runtime.Host.World.TryGet(uid, out HostObject obj) ? obj.Location : null;
        }

        private static double Global(Location location, bool x)
            => x
                ? ((location.LandblockCell >> 24) & 0xFF) * 192.0 + location.X
                : ((location.LandblockCell >> 16) & 0xFF) * 192.0 + location.Y;
    }

    /// <summary>
    /// A set of world objects, chosen by a filter when it is made and walked like Decal's:
    /// either with foreach, or with <see cref="MoveNext"/> and <see cref="Current"/>.
    /// </summary>
    /// <remarks>
    /// Taken as a snapshot when made, or when <see cref="SetFilter"/> changes the filter, so a
    /// plugin that walks the collection while handling an event that adds objects does not
    /// find the collection changing under it.
    /// </remarks>
    public class WorldObjectCollection : DisposableByRefObject, IEnumerable<WorldObject>
    {
        private readonly WorldFilter _world;
        private List<HostObject> _objects;
        private int _position = -1;

        internal WorldObjectCollection(WorldFilter world, WorldObjectCollectionFilter filter)
        {
            _world = world;
            SetFilter(filter);
        }

        public int Count => _objects.Count;

        /// <summary>How many things the collection holds, counting every item of every stack.</summary>
        public int Quantity => _objects.Sum(o => Math.Max(1, (int)(o.StackSize ?? 1)));

        public WorldObject First => _objects.Count > 0 ? new WorldObject(_objects[0]) : null;

        public WorldObject Current => _position >= 0 && _position < _objects.Count ? new WorldObject(_objects[_position]) : null;

        public void SetFilter(WorldObjectCollectionFilter filter)
        {
            filter ??= new ByAllFilter();
            DecalRuntime runtime = _world.Runtime;
            _objects = runtime.Host.World.Objects.Where(o => filter.Matches(o, runtime)).ToList();
            _position = -1;
        }

        public bool MoveNext() => ++_position < _objects.Count;

        public void Reset() => _position = -1;

        public IEnumerator<WorldObject> GetEnumerator()
        {
            foreach (HostObject obj in _objects)
                yield return new WorldObject(obj);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>Which objects a <see cref="WorldObjectCollection"/> holds.</summary>
    public abstract class WorldObjectCollectionFilter
    {
        protected WorldObjectCollectionFilter()
        {
        }

        internal abstract bool Matches(HostObject obj, DecalRuntime runtime);

        /// <summary>Whether <paramref name="obj"/> is inside <paramref name="owner"/>, however deep, or worn by it.</summary>
        internal static bool IsOwnedBy(HostObject obj, uint owner, DecalRuntime runtime)
        {
            HostObject current = obj;
            for (int depth = 0; current != null && depth < 8; depth++)
            {
                if (current.WielderId == owner || current.ContainerId == owner)
                    return true;

                if (!current.ContainerId.HasValue)
                    return false;

                runtime.Host.World.TryGet(current.ContainerId.Value, out current);
            }

            return false;
        }
    }

    public class ByAllFilter : WorldObjectCollectionFilter
    {
        internal override bool Matches(HostObject obj, DecalRuntime runtime) => true;
    }

    public class ByCategoryFilter : WorldObjectCollectionFilter
    {
        public ByCategoryFilter()
        {
        }

        public ByCategoryFilter(int category)
        {
            Category = category;
        }

        public int Category { get; set; }

        internal override bool Matches(HostObject obj, DecalRuntime runtime) => (obj.ItemType & unchecked((uint)Category)) != 0;
    }

    public class ByContainerFilter : WorldObjectCollectionFilter
    {
        public ByContainerFilter()
        {
        }

        public ByContainerFilter(int container)
        {
            Container = container;
        }

        public int Container { get; set; }

        internal override bool Matches(HostObject obj, DecalRuntime runtime) => obj.ContainerId == unchecked((uint)Container);
    }

    public class ByInventoryFilter : WorldObjectCollectionFilter
    {
        internal override bool Matches(HostObject obj, DecalRuntime runtime)
        {
            uint me = runtime.Host.Character.Id;
            return me != 0 && obj.Id != me && IsOwnedBy(obj, me, runtime);
        }
    }

    public class ByLandscapeFilter : WorldObjectCollectionFilter
    {
        internal override bool Matches(HostObject obj, DecalRuntime runtime)
            => obj.Location.HasValue && !obj.ContainerId.HasValue && !obj.WielderId.HasValue;
    }

    public class ByNameFilter : WorldObjectCollectionFilter
    {
        public ByNameFilter()
        {
        }

        public ByNameFilter(string name)
        {
            Name = name;
        }

        public string Name { get; set; }

        internal override bool Matches(HostObject obj, DecalRuntime runtime)
            => string.Equals(obj.Name, Name, StringComparison.OrdinalIgnoreCase);
    }

    public class ByNameSubStringFilter : ByNameFilter
    {
        internal override bool Matches(HostObject obj, DecalRuntime runtime)
            => !string.IsNullOrEmpty(Name) && obj.Name != null && obj.Name.Contains(Name, StringComparison.OrdinalIgnoreCase);
    }

    public class ByObjectClassFilter : WorldObjectCollectionFilter
    {
        public ByObjectClassFilter()
        {
        }

        public ByObjectClassFilter(ObjectClass objClass)
        {
            ObjectClass = objClass;
        }

        public ObjectClass ObjectClass { get; set; }

        internal override bool Matches(HostObject obj, DecalRuntime runtime) => ObjectClassifier.Classify(obj) == ObjectClass;
    }

    public class ByOwnerFilter : WorldObjectCollectionFilter
    {
        public ByOwnerFilter()
        {
        }

        public ByOwnerFilter(int owner)
        {
            Owner = owner;
        }

        public int Owner { get; set; }

        internal override bool Matches(HostObject obj, DecalRuntime runtime) => IsOwnedBy(obj, unchecked((uint)Owner), runtime);
    }

    /// <summary>A merchant's stock. The host does not decode vendor windows, so one is never open.</summary>
    public class Vendor : DisposableByRefObject, IEnumerable<WorldObject>
    {
        internal Vendor()
        {
        }

        public int MerchantId => 0;

        public int MaxValue => 0;

        public float SellRate => 0;

        public float BuyRate => 0;

        public int Categories => 0;

        public int Count => 0;

        public int Quantity => 0;

        public WorldObject First => null;

        public WorldObject Current => null;

        public WorldObject this[int id] => null;

        public void SetFilter(WorldObjectCollectionFilter filter)
        {
        }

        public bool MoveNext() => false;

        public void Reset()
        {
        }

        public IEnumerator<WorldObject> GetEnumerator() => Enumerable.Empty<WorldObject>().GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
