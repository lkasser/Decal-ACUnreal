using System;
using System.Collections.Generic;
using System.Linq;
using AC.Host.Decoding;
using AC.Host.World;
using HostObject = AC.Host.World.WorldObject;

namespace Decal.Adapter.Wrappers
{
    /// <summary>
    /// One object in the world, as Decal's world filter described it.
    /// </summary>
    /// <remarks>
    /// A view onto the host's object, not a copy: every read is of what the host knows now,
    /// which is how Decal's wrapper behaved too, since it read through to the filter's live
    /// object. A new wrapper is made each time one is asked for - Decal did the same - so two
    /// wrappers for one object are equal by <see cref="Id"/>, not by reference.
    /// </remarks>
    public class WorldObject : DisposableByRefObject
    {
        private readonly HostObject _obj;

        internal WorldObject(HostObject obj)
        {
            _obj = obj ?? throw new ArgumentNullException(nameof(obj));
        }

        internal HostObject Underlying => _obj;

        public int Id => unchecked((int)_obj.Id);

        public string Name => _obj.Name ?? string.Empty;

        public ObjectClass ObjectClass => ObjectClassifier.Classify(_obj);

        /// <summary>The weenie class id - what kind of thing this is, as opposed to which one.</summary>
        public int Type => unchecked((int)_obj.WeenieClassId);

        /// <summary>
        /// The icon's portal file number without its 0x06000000 prefix, as Decal gave it:
        /// plugins add the prefix back themselves when they draw it.
        /// </summary>
        public int Icon => unchecked((int)(_obj.IconId & 0x00FFFFFF));

        public int Container => unchecked((int)(_obj.ContainerId ?? 0));

        /// <summary>The object's item type bits.</summary>
        public int Category => unchecked((int)_obj.ItemType);

        /// <summary>The object description flags: player, door, corpse, vendor and the like.</summary>
        public int Behavior => unchecked((int)_obj.DescriptionFlags);

        public int GameDataFlags1 => unchecked((int)_obj.WeenieFlags);

        public int PhysicsDataFlags => unchecked((int)_obj.PhysicsFlags);

        /// <summary>Whether the server has answered an appraisal of it.</summary>
        public bool HasIdData => _obj.HasAppraisalData;

        /// <summary>When it was last appraised. The host keeps no such time, so 0.</summary>
        public int LastIdTime => 0;

        public int SpellCount => _obj.SpellIds.Count;

        /// <summary>Spells cast on the item itself. The host does not track those, so none.</summary>
        public int ActiveSpellCount => 0;

        public int Spell(int index) => index >= 0 && index < _obj.SpellIds.Count ? unchecked((int)_obj.SpellIds[index]) : 0;

        public int ActiveSpell(int index) => 0;

        public List<int> BoolKeys => _obj.Bools.Keys.Select(k => unchecked((int)k)).ToList();

        public List<int> LongKeys => _obj.Ints.Keys.Select(k => unchecked((int)k)).ToList();

        public List<int> DoubleKeys => _obj.Floats.Keys.Select(k => unchecked((int)k)).ToList();

        public List<int> StringKeys => _obj.Strings.Keys.Select(k => unchecked((int)k)).ToList();

        /// <summary>Where it is on the map, or null for something with no place of its own - an item in a pack.</summary>
        public CoordsObject Coordinates() => _obj.Location.HasValue ? CoordsObject.From(_obj.Location.Value) : null;

        /// <summary>Its position within its landblock.</summary>
        public Vector3Object Offset()
            => _obj.Location is Location l ? new Vector3Object(l.X, l.Y, l.Z) : null;

        /// <summary>Its position in world units from the corner of the map, which is what distances are measured in.</summary>
        public Vector3Object RawCoordinates()
            => _obj.Location is Location l
                ? new Vector3Object(((l.LandblockCell >> 24) & 0xFF) * 192.0 + l.X, ((l.LandblockCell >> 16) & 0xFF) * 192.0 + l.Y, l.Z)
                : null;

        public Vector4Object Orientation()
            => _obj.Location is Location l ? new Vector4Object(l.QW, l.QX, l.QY, l.QZ) : null;

        // ------------------------------------------------------------------- values

        public int Values(LongValueKey index) => Values(index, 0);

        public int Values(LongValueKey index, int defaultValue) => TryLong(index, out int value) ? value : defaultValue;

        public double Values(DoubleValueKey index) => Values(index, 0.0);

        public double Values(DoubleValueKey index, double defaultValue) => TryDouble(index, out double value) ? value : defaultValue;

        public string Values(StringValueKey index) => Values(index, string.Empty);

        public string Values(StringValueKey index, string defaultValue) => TryString(index, out string value) ? value : defaultValue;

        public bool Values(BoolValueKey index) => Values(index, false);

        public bool Values(BoolValueKey index, bool defaultValue) => TryBool(index, out bool value) ? value : defaultValue;

        public bool Exists(LongValueKey index) => TryLong(index, out _);

        public bool Exists(LongValueKey index, out int pValue) => TryLong(index, out pValue);

        public bool Exists(DoubleValueKey index) => TryDouble(index, out _);

        public bool Exists(DoubleValueKey index, out double pValue) => TryDouble(index, out pValue);

        public bool Exists(StringValueKey index) => TryString(index, out _);

        public bool Exists(StringValueKey index, out string pValue) => TryString(index, out pValue);

        public bool Exists(BoolValueKey index) => TryBool(index, out _);

        public bool Exists(BoolValueKey index, out bool pValue) => TryBool(index, out pValue);

        /// <summary>
        /// A whole-number value. Decal's own keys - those from 0x0D000000 up - are answered from
        /// the object's description; every other key is the game's own property id, answered
        /// from the properties an appraisal brought, or from the description when the same
        /// fact came that way, as a value or a burden does.
        /// </summary>
        private bool TryLong(LongValueKey key, out int value)
        {
            uint? found = key switch
            {
                LongValueKey.Type => _obj.WeenieClassId,
                LongValueKey.Icon => _obj.IconId & 0x00FFFFFF,
                LongValueKey.Container => _obj.ContainerId,
                LongValueKey.Landblock => _obj.Location?.LandblockCell,
                LongValueKey.ItemSlots => _obj.ItemCapacity,
                LongValueKey.PackSlots => _obj.ContainerCapacity,
                LongValueKey.StackCount => _obj.StackSize,
                LongValueKey.StackMax => _obj.MaxStackSize,
                LongValueKey.AssociatedSpell => _obj.SpellId,
                LongValueKey.Wielder => _obj.WielderId,
                LongValueKey.WieldingSlot => _obj.CurrentlyWieldedLocation,
                LongValueKey.Monarch => _obj.Monarch,
                LongValueKey.Coverage => _obj.Priority,
                LongValueKey.EquipableSlots => _obj.ValidLocations,
                LongValueKey.EquipType => _obj.CombatUse.HasValue ? unchecked((uint)_obj.CombatUse.Value) : null,
                LongValueKey.IconOutline => _obj.UiEffects,
                LongValueKey.MissileType => _obj.AmmoType,
                LongValueKey.UsageMask => _obj.Usable,
                LongValueKey.HouseOwner => _obj.HouseOwner,
                LongValueKey.HookMask => _obj.HookItemTypes,
                LongValueKey.HookType => _obj.HookType,
                LongValueKey.Model => _obj.SetupId,
                LongValueKey.Flags => _obj.WeenieFlags,
                LongValueKey.CreateFlags1 => _obj.WeenieFlags,
                LongValueKey.CreateFlags2 => _obj.WeenieFlags2,
                LongValueKey.Category => _obj.ItemType,
                LongValueKey.Behavior => _obj.DescriptionFlags,
                LongValueKey.SpellCount => (uint)_obj.SpellIds.Count,
                LongValueKey.PhysicsDataFlags => _obj.PhysicsFlags,
                LongValueKey.IconOverlay => _obj.IconOverlay,
                LongValueKey.IconUnderlay => _obj.IconUnderlay,
                LongValueKey.WeapSpeed => _obj.Appraisal.HasWeapon ? _obj.Appraisal.WeaponTime : null,
                LongValueKey.EquipSkill => _obj.Appraisal.HasWeapon ? _obj.Appraisal.WeaponSkill : null,
                LongValueKey.DamageType => _obj.Appraisal.HasWeapon ? _obj.Appraisal.WeaponDamageType : null,
                LongValueKey.MaxDamage => _obj.Appraisal.HasWeapon ? _obj.Appraisal.WeaponDamage : null,
                _ => null,
            };

            if (found.HasValue)
            {
                value = unchecked((int)found.Value);
                return true;
            }

            if ((int)key >= 0x0D000000)
            {
                // One of Decal's own keys the host has nothing for - a legacy slot, an unknown
                // flag word. Absent rather than zero, so Exists tells the truth.
                value = 0;
                return false;
            }

            if (_obj.Ints.TryGetValue((uint)key, out value))
                return true;

            // Facts the object's description carries, which an appraisal repeats as properties
            // only sometimes.
            int? described = key switch
            {
                LongValueKey.Value => _obj.Value,
                LongValueKey.Burden => _obj.Burden,
                LongValueKey.Material => _obj.MaterialType.HasValue ? unchecked((int)_obj.MaterialType.Value) : null,
                LongValueKey.UsesRemaining => _obj.Structure,
                LongValueKey.UsesTotal => _obj.MaxStructure,
                LongValueKey.EquippedSlots => _obj.CurrentlyWieldedLocation.HasValue ? unchecked((int)_obj.CurrentlyWieldedLocation.Value) : null,
                LongValueKey.Workmanship => _obj.Workmanship.HasValue ? (int)_obj.Workmanship.Value : null,
                _ => null,
            };

            value = described ?? 0;
            return described.HasValue;
        }

        private bool TryDouble(DoubleValueKey key, out double value)
        {
            float[] protection = _obj.Appraisal.HasArmor ? _obj.Appraisal.ArmorProtection : Array.Empty<float>();

            double? found = key switch
            {
                // The host holds armour protection as slash, pierce, bludgeon, cold, fire, acid,
                // nether, lightning; Decal named the same numbers by kind.
                DoubleValueKey.SlashProt => At(protection, 0),
                DoubleValueKey.PierceProt => At(protection, 1),
                DoubleValueKey.BludgeonProt => At(protection, 2),
                DoubleValueKey.ColdProt => At(protection, 3),
                DoubleValueKey.FireProt => At(protection, 4),
                DoubleValueKey.AcidProt => At(protection, 5),
                DoubleValueKey.LightningProt => At(protection, 7),
                DoubleValueKey.Heading => _obj.Location is Location l ? HeadingOf(l) : null,
                DoubleValueKey.ApproachDistance => _obj.UseRadius,
                DoubleValueKey.SalvageWorkmanship => _obj.Workmanship,
                DoubleValueKey.Scale => _obj.Scale,
                DoubleValueKey.Variance => _obj.Appraisal.HasWeapon ? _obj.Appraisal.WeaponVariance : null,
                DoubleValueKey.AttackBonus => _obj.Appraisal.HasWeapon ? _obj.Appraisal.WeaponOffense : null,
                DoubleValueKey.Range => _obj.Appraisal.HasWeapon ? _obj.Appraisal.WeaponMaxVelocity : null,
                DoubleValueKey.DamageBonus => _obj.Appraisal.HasWeapon ? _obj.Appraisal.WeaponDamageMod : null,
                _ => null,
            };

            if (found.HasValue)
            {
                value = found.Value;
                return true;
            }

            if ((int)key >= 0x0A000000)
            {
                value = 0;
                return false;
            }

            return _obj.Floats.TryGetValue((uint)key, out value);
        }

        private bool TryString(StringValueKey key, out string value)
        {
            if (key == StringValueKey.SecondaryName)
            {
                value = _obj.PluralName;
                return value != null;
            }

            if (_obj.Strings.TryGetValue((uint)key, out value))
                return true;

            if (key == StringValueKey.Name && !string.IsNullOrEmpty(_obj.Name))
            {
                value = _obj.Name;
                return true;
            }

            value = null;
            return false;
        }

        private bool TryBool(BoolValueKey key, out bool value)
        {
            switch (key)
            {
                case BoolValueKey.Lockable:
                    value = _obj.HasItemType(ItemTypes.Lockable);
                    return true;
                case BoolValueKey.Inscribable:
                    value = _obj.HasDescriptionFlag(DescriptionFlags.Inscribable);
                    return true;
                default:
                    return _obj.Bools.TryGetValue((uint)key, out value);
            }
        }

        private static double? At(float[] values, int index) => index < values.Length ? values[index] : null;

        /// <summary>Degrees clockwise from north, from the rotation the game gives a position.</summary>
        internal static double HeadingOf(Location location)
        {
            // A heading h is a rotation of -h about z: w = cos(-h/2), z = sin(-h/2).
            double radians = -2.0 * Math.Atan2(location.QZ, location.QW);
            double degrees = radians * 180.0 / Math.PI % 360.0;
            return degrees < 0 ? degrees + 360.0 : degrees;
        }
    }

    /// <summary>
    /// Decal's rough kinds of object, worked out from the item type bits and description
    /// flags the server sends.
    /// </summary>
    /// <remarks>
    /// Decal decided these from the same two fields, with a few further guesses by name for
    /// the kinds the protocol never names - salvage, a healing kit. The order matters: a
    /// corpse is a container, and a player is a creature, and the narrower answer wins.
    /// </remarks>
    internal static class ObjectClassifier
    {
        public static ObjectClass Classify(HostObject obj)
        {
            uint flags = obj.DescriptionFlags;
            uint type = obj.ItemType;

            if ((flags & DescriptionFlags.Player) != 0) return ObjectClass.Player;
            if ((flags & DescriptionFlags.Corpse) != 0) return ObjectClass.Corpse;
            if ((flags & DescriptionFlags.Vendor) != 0) return ObjectClass.Vendor;
            if ((flags & DescriptionFlags.Door) != 0) return ObjectClass.Door;
            if ((flags & DescriptionFlags.LifeStone) != 0 || (type & ItemTypes.LifeStone) != 0) return ObjectClass.Lifestone;
            if ((flags & DescriptionFlags.Portal) != 0 || (type & ItemTypes.Portal) != 0) return ObjectClass.Portal;
            if ((flags & DescriptionFlags.Healer) != 0) return ObjectClass.HealingKit;
            if ((flags & DescriptionFlags.Lockpick) != 0) return ObjectClass.Lockpick;
            if ((flags & DescriptionFlags.Book) != 0) return ObjectClass.Book;

            if ((type & ItemTypes.Creature) != 0)
                return (flags & DescriptionFlags.Attackable) != 0 ? ObjectClass.Monster : ObjectClass.Npc;

            if ((type & ItemTypes.MeleeWeapon) != 0) return ObjectClass.MeleeWeapon;
            if ((type & ItemTypes.MissileWeapon) != 0) return ObjectClass.MissileWeapon;
            if ((type & ItemTypes.Caster) != 0) return ObjectClass.WandStaffOrb;
            if ((type & ItemTypes.Armor) != 0) return ObjectClass.Armor;
            if ((type & ItemTypes.Clothing) != 0) return ObjectClass.Clothing;
            if ((type & ItemTypes.Jewelry) != 0) return ObjectClass.Jewelry;
            if ((type & ItemTypes.Food) != 0) return ObjectClass.Food;
            if ((type & ItemTypes.Money) != 0) return ObjectClass.Money;
            if ((type & ItemTypes.Container) != 0) return ObjectClass.Container;
            if ((type & ItemTypes.Gem) != 0) return ObjectClass.Gem;
            if ((type & ItemTypes.SpellComponents) != 0) return ObjectClass.SpellComponent;
            if ((type & ItemTypes.Key) != 0) return ObjectClass.Key;
            if ((type & ItemTypes.PromissoryNote) != 0) return ObjectClass.TradeNote;
            if ((type & ItemTypes.ManaStone) != 0) return ObjectClass.ManaStone;
            if ((type & ItemTypes.Service) != 0) return ObjectClass.Services;
            if ((type & ItemTypes.CraftCookingBase) != 0) return ObjectClass.BaseCooking;
            if ((type & ItemTypes.CraftAlchemyBase) != 0) return ObjectClass.BaseAlchemy;
            if ((type & ItemTypes.CraftFletchingBase) != 0) return ObjectClass.BaseFletching;
            if ((type & ItemTypes.CraftAlchemyIntermediate) != 0) return ObjectClass.CraftedAlchemy;
            if ((type & ItemTypes.CraftFletchingIntermediate) != 0) return ObjectClass.CraftedFletching;
            if ((type & ItemTypes.TinkeringMaterial) != 0) return ObjectClass.Salvage;
            if ((type & ItemTypes.TinkeringTool) != 0) return ObjectClass.Ust;
            if ((type & ItemTypes.Writable) != 0) return obj.Name.Contains("Scroll", StringComparison.OrdinalIgnoreCase) ? ObjectClass.Scroll : ObjectClass.Journal;

            if ((type & ItemTypes.Misc) != 0)
            {
                if (obj.Name.Contains("Healing Kit", StringComparison.OrdinalIgnoreCase)) return ObjectClass.HealingKit;
                if (obj.Name.Contains("Lockpick", StringComparison.OrdinalIgnoreCase)) return ObjectClass.Lockpick;
                if (obj.Name.Contains("Bundle", StringComparison.OrdinalIgnoreCase)) return ObjectClass.Bundle;
                if (obj.Name.Contains("Foci", StringComparison.OrdinalIgnoreCase) || obj.Name.Contains("Focus", StringComparison.OrdinalIgnoreCase)) return ObjectClass.Foci;
                if (obj.Name.StartsWith("Plant", StringComparison.OrdinalIgnoreCase)) return ObjectClass.Plant;
                return ObjectClass.Misc;
            }

            return ObjectClass.Unknown;
        }
    }
}
