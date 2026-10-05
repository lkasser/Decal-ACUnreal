using System;
using System.Collections.Generic;

namespace AC.Host.World
{
    /// <summary>Where an object is in the world.</summary>
    public readonly struct Location
    {
        public Location(uint landblockCell, float x, float y, float z, float qw, float qx, float qy, float qz)
        {
            LandblockCell = landblockCell;
            X = x;
            Y = y;
            Z = z;
            QW = qw;
            QX = qx;
            QY = qy;
            QZ = qz;
        }

        /// <summary>Landblock id in the high 16 bits, cell in the low 16.</summary>
        public uint LandblockCell { get; }

        public uint Landblock => LandblockCell >> 16;

        public float X { get; }

        public float Y { get; }

        public float Z { get; }

        public float QW { get; }

        public float QX { get; }

        public float QY { get; }

        public float QZ { get; }

        public override string ToString() => $"0x{LandblockCell:X8} ({X:F1}, {Y:F1}, {Z:F1})";
    }

    /// <summary>A velocity, in the same axes and units as <see cref="Location"/>, per second.</summary>
    public readonly struct Velocity
    {
        public Velocity(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public float X { get; }

        public float Y { get; }

        public float Z { get; }

        /// <summary>Speed across the ground, leaving the vertical component out.</summary>
        public float HorizontalSpeed => MathF.Sqrt((X * X) + (Y * Y));

        public bool IsZero => X == 0 && Y == 0 && Z == 0;

        public override string ToString() => $"({X:F1}, {Y:F1}, {Z:F1})";
    }

    /// <summary>One palette slot of an object's appearance, as sent in its model data.</summary>
    public readonly struct PaletteSlot
    {
        public PaletteSlot(uint paletteId, byte offset, byte length)
        {
            PaletteId = paletteId;
            Offset = offset;
            Length = length;
        }

        public uint PaletteId { get; }

        public byte Offset { get; }

        public byte Length { get; }
    }

    /// <summary>The appraisal-only profiles that come with an identify response.</summary>
    public sealed class AppraisalProfiles
    {
        public bool HasWeapon { get; internal set; }

        public uint WeaponDamageType { get; internal set; }

        public uint WeaponTime { get; internal set; }

        public uint WeaponSkill { get; internal set; }

        public uint WeaponDamage { get; internal set; }

        public double WeaponVariance { get; internal set; }

        public double WeaponDamageMod { get; internal set; }

        public double WeaponLength { get; internal set; }

        public double WeaponMaxVelocity { get; internal set; }

        public double WeaponOffense { get; internal set; }

        public uint WeaponMaxVelocityEstimated { get; internal set; }

        public bool HasArmor { get; internal set; }

        /// <summary>Slash, pierce, bludgeon, cold, fire, acid, nether, lightning.</summary>
        public float[] ArmorProtection { get; internal set; } = Array.Empty<float>();

        public bool HasArmorLevels { get; internal set; }

        /// <summary>Head, chest, abdomen, upper arm, lower arm, hand, upper leg, lower leg, foot.</summary>
        public uint[] ArmorLevels { get; internal set; } = Array.Empty<uint>();

        public bool HasCreature { get; internal set; }

        public uint CreatureHealth { get; internal set; }

        public uint CreatureHealthMax { get; internal set; }
    }

    /// <summary>
    /// Everything known about one object in the world.
    /// </summary>
    /// <remarks>
    /// Two kinds of data live here and it matters which is which. The typed fields
    /// come from the object-creation message and are always present for a visible
    /// object. The property dictionaries, keyed by the server's property ids, fill
    /// in from appraisal responses and property-update messages and may be empty
    /// until an identify has been done. The property ids are the same numbers Decal
    /// used for its low-range value keys, which is why the VirindiTank adapter can
    /// pass them straight through.
    ///
    /// Mutable, and mutated only on the host's game thread.
    /// </remarks>
    public sealed class WorldObject
    {
        public WorldObject(uint id)
        {
            Id = id;
        }

        public uint Id { get; }

        public string Name { get; internal set; } = string.Empty;

        public string PluralName { get; internal set; }

        public uint WeenieClassId { get; internal set; }

        public uint IconId { get; internal set; }

        /// <summary>The server's item type bitfield.</summary>
        public uint ItemType { get; internal set; }

        /// <summary>The object description flags (player, vendor, corpse, door...).</summary>
        public uint DescriptionFlags { get; internal set; }

        /// <summary>The weenie header flags the creation message carried.</summary>
        public uint WeenieFlags { get; internal set; }

        public uint WeenieFlags2 { get; internal set; }

        public byte? ItemCapacity { get; internal set; }

        public byte? ContainerCapacity { get; internal set; }

        public ushort? AmmoType { get; internal set; }

        public int? Value { get; internal set; }

        public uint? Usable { get; internal set; }

        public float? UseRadius { get; internal set; }

        public uint? TargetType { get; internal set; }

        public uint? UiEffects { get; internal set; }

        public sbyte? CombatUse { get; internal set; }

        public ushort? Structure { get; internal set; }

        public ushort? MaxStructure { get; internal set; }

        public ushort? StackSize { get; internal set; }

        public ushort? MaxStackSize { get; internal set; }

        /// <summary>Id of the container holding this object, when it is in one.</summary>
        public uint? ContainerId
        {
            get => _containerId;
            internal set
            {
                if (_containerId == value)
                    return;

                _containerId = value;
                ArrivalOrder = NextArrival();
            }
        }

        private uint? _containerId;

        /// <summary>
        /// When the object arrived where it is, as a count that only goes up: taken afresh when
        /// the object is created or sent again in full, and whenever its container changes. Of
        /// two things in one pack, the one with the higher count came later - the order a
        /// container's contents were learned in, which is how Virindi Tank's inventory tracker
        /// listed them. The host does not keep slot order.
        /// </summary>
        public long ArrivalOrder { get; internal set; }

        private static long _arrivals;

        /// <summary>The next arrival count, shared by every world so that it only goes up.</summary>
        internal static long NextArrival() => System.Threading.Interlocked.Increment(ref _arrivals);

        /// <summary>Id of the creature wielding this object, when it is equipped.</summary>
        public uint? WielderId { get; internal set; }

        public uint? ValidLocations { get; internal set; }

        public uint? CurrentlyWieldedLocation { get; internal set; }

        /// <summary>Clothing priority, which is the armour coverage mask.</summary>
        public uint? Priority { get; internal set; }

        public byte? RadarColor { get; internal set; }

        public byte? RadarBehavior { get; internal set; }

        public ushort? PhysicsScript { get; internal set; }

        public float? Workmanship { get; internal set; }

        public ushort? Burden { get; internal set; }

        public ushort? SpellId { get; internal set; }

        public uint? HouseOwner { get; internal set; }

        public uint? HookItemTypes { get; internal set; }

        public uint? Monarch { get; internal set; }

        public ushort? HookType { get; internal set; }

        public uint? IconOverlay { get; internal set; }

        public uint? IconUnderlay { get; internal set; }

        public uint? MaterialType { get; internal set; }

        public int? CooldownId { get; internal set; }

        public double? CooldownDuration { get; internal set; }

        public uint? PetOwner { get; internal set; }

        /// <summary>Physics-description flags from the creation message.</summary>
        public uint PhysicsFlags { get; internal set; }

        public uint PhysicsState { get; internal set; }

        public Location? Location { get; internal set; }

        public uint? ParentId { get; internal set; }

        public uint? ParentLocation { get; internal set; }

        public float? Scale { get; internal set; }

        public uint SetupId { get; internal set; }

        public uint MotionTableId { get; internal set; }

        /// <summary>Current health as a fraction of maximum, from UpdateHealth events.</summary>
        public float? HealthFraction { get; internal set; }

        /// <summary>
        /// What the object is doing, from the most recent motion message. Null until
        /// one has been seen for it.
        /// </summary>
        public MovementState Movement { get; internal set; }

        /// <summary>
        /// The velocity the server last announced for the object. Null until it has.
        /// </summary>
        /// <remarks>
        /// The server announces one for a jump and for a projectile coming to rest,
        /// and says nothing when the jumper lands. So this is what the object was last
        /// set moving at, not a running account of how fast it is going.
        /// </remarks>
        public Velocity? Velocity { get; internal set; }

        public List<PaletteSlot> Palettes { get; } = new List<PaletteSlot>();

        public uint PaletteBaseId { get; internal set; }

        public Dictionary<uint, int> Ints { get; } = new Dictionary<uint, int>();

        public Dictionary<uint, long> Int64s { get; } = new Dictionary<uint, long>();

        public Dictionary<uint, bool> Bools { get; } = new Dictionary<uint, bool>();

        public Dictionary<uint, double> Floats { get; } = new Dictionary<uint, double>();

        public Dictionary<uint, string> Strings { get; } = new Dictionary<uint, string>();

        public Dictionary<uint, uint> DataIds { get; } = new Dictionary<uint, uint>();

        public Dictionary<uint, uint> InstanceIds { get; } = new Dictionary<uint, uint>();

        /// <summary>True once an identify response for this object has been seen.</summary>
        public bool HasAppraisalData { get; internal set; }

        /// <summary>Whether the last appraisal succeeded (a failed one carries no data).</summary>
        public bool AppraisalSucceeded { get; internal set; }

        public List<uint> SpellIds { get; } = new List<uint>();

        public AppraisalProfiles Appraisal { get; } = new AppraisalProfiles();

        /// <summary>Host clock when the object was last created, updated or appraised.</summary>
        public DateTimeOffset LastSeen { get; internal set; }

        /// <summary>How many times the object has been created or updated in full.</summary>
        public int CreateCount { get; internal set; }

        public bool HasDescriptionFlag(uint flag) => (DescriptionFlags & flag) != 0;

        public bool HasItemType(uint bit) => (ItemType & bit) != 0;

        /// <summary>
        /// Resets everything that a full creation message replaces, keeping what it
        /// does not carry - appraisal data survives a re-create of the same object.
        /// </summary>
        internal void ResetCreationFields()
        {
            PluralName = null;
            ItemCapacity = null;
            ContainerCapacity = null;
            AmmoType = null;
            Value = null;
            Usable = null;
            UseRadius = null;
            TargetType = null;
            UiEffects = null;
            CombatUse = null;
            Structure = null;
            MaxStructure = null;
            StackSize = null;
            MaxStackSize = null;
            ContainerId = null;
            WielderId = null;
            ValidLocations = null;
            CurrentlyWieldedLocation = null;
            Priority = null;
            RadarColor = null;
            RadarBehavior = null;
            PhysicsScript = null;
            Workmanship = null;
            Burden = null;
            SpellId = null;
            HouseOwner = null;
            HookItemTypes = null;
            Monarch = null;
            HookType = null;
            IconOverlay = null;
            IconUnderlay = null;
            MaterialType = null;
            CooldownId = null;
            CooldownDuration = null;
            PetOwner = null;
            ParentId = null;
            ParentLocation = null;
            Scale = null;
            Palettes.Clear();
            PaletteBaseId = 0;
        }

        public override string ToString() => $"0x{Id:X8} \"{Name}\" wcid={WeenieClassId} type=0x{ItemType:X}";
    }

    /// <summary>The contents of a container as the server listed them.</summary>
    public sealed class ContainerContents
    {
        public ContainerContents(uint containerId, IReadOnlyList<ContainedItem> items)
        {
            ContainerId = containerId;
            Items = items;
        }

        public uint ContainerId { get; }

        public IReadOnlyList<ContainedItem> Items { get; }
    }

    public readonly struct ContainedItem
    {
        public ContainedItem(uint id, uint containerType)
        {
            Id = id;
            ContainerType = containerType;
        }

        public uint Id { get; }

        /// <summary>0 = ordinary item, 1 = a pack, 2 = a foci.</summary>
        public uint ContainerType { get; }
    }

    /// <summary>
    /// The server would not move an item, and left it where it was: InventoryServerSaveFailed.
    /// </summary>
    /// <remarks>
    /// ACE sends this for every move it turns down - a pick-up from a corpse or the ground, a
    /// move between packs, a drop - before or after walking to the item. The error is often
    /// zero: a full pack ("Unable to put ... into container"), too much to carry ("You are too
    /// encumbered to carry that!"), an item that cannot be moved at all (WeenieError Stuck) or
    /// the character being too busy (YoureTooBusy) each come with zero here and their reason
    /// in a chat line or a WeenieError of their own. A move that could not walk to the item
    /// gives ActionCancelled.
    /// </remarks>
    public sealed class MoveRefusal
    {
        public MoveRefusal(uint itemId, uint error)
        {
            ItemId = itemId;
            Error = error;
        }

        /// <summary>The item that stayed where it was.</summary>
        public uint ItemId { get; }

        /// <summary>The server's WeenieError, or zero.</summary>
        public uint Error { get; }

        public override string ToString() => $"0x{ItemId:X8} not moved (0x{Error:X4})";
    }
}
