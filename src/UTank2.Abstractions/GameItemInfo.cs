///////////////////////////////////////////////////////////////////////////////
// File: GameItemInfo.cs
//
// The item-inspection contract a loot plugin is handed for each candidate item.
//
// Clean-room reconstruction of the uTank2.LootPlugins surface that VTClassic
// consumes, derived solely from its call sites in virindi_public r162. The
// original utank2-i.dll is not redistributable and is not required to build
// against this contract.
///////////////////////////////////////////////////////////////////////////////

using System.Collections.ObjectModel;
using System.Drawing;

namespace uTank2.LootPlugins
{
    /// <summary>
    /// Everything a loot rule may ask about one item. A host implements this over
    /// whatever item representation its client has: Decal's <c>WorldObject</c> for
    /// the retail client, or a decoded server object for a modern client.
    /// </summary>
    public abstract class GameItemInfo
    {
        /// <summary>
        /// The item's object id, unique within the session. The host uses the ids a
        /// plugin returns to act on specific items.
        /// </summary>
        public abstract int Id { get; }

        /// <summary>Object class as classified by the client.</summary>
        public abstract ObjectClass ObjectClass { get; }

        /// <summary>
        /// True once the server's appraisal (identify) response for this item has
        /// arrived. Rules that read appraisal-only properties are meaningless
        /// before this is set.
        /// </summary>
        public abstract bool HasIDData { get; }

        /// <summary>Spells on the item, in server order.</summary>
        public abstract ReadOnlyCollection<MySpell> Spells { get; }

        /// <summary>Palette (colour) slots of the item's appearance.</summary>
        public abstract ReadOnlyCollection<PaletteData> Palettes { get; }

        public abstract bool KeyExistsInt(int key);

        public abstract bool KeyExistsDouble(int key);

        public abstract int GetValueInt(int key, int defaultValue);

        public abstract double GetValueDouble(int key, double defaultValue);

        public abstract string GetValueString(int key, string defaultValue);

        public int GetValueInt(IntValueKey key, int defaultValue)
            => GetValueInt((int)key, defaultValue);

        public double GetValueDouble(DoubleValueKey key, double defaultValue)
            => GetValueDouble((int)key, defaultValue);

        public string GetValueString(StringValueKey key, string defaultValue)
            => GetValueString((int)key, defaultValue);

        public bool KeyExistsInt(IntValueKey key) => KeyExistsInt((int)key);

        public bool KeyExistsDouble(DoubleValueKey key) => KeyExistsDouble((int)key);

        /// <summary>One palette slot of an item's appearance.</summary>
        public sealed class PaletteData
        {
            public PaletteData(int palette, Color exampleColor, int offset, int length)
            {
                Palette = palette;
                ExampleColor = exampleColor;
                Offset = offset;
                Length = length;
            }

            /// <summary>
            /// Palette id applied to the slot. Rules compare only the low 24 bits,
            /// so the high byte may carry whatever the client puts there.
            /// </summary>
            public int Palette { get; }

            /// <summary>
            /// A representative colour for the slot. The colour rules compare this
            /// in HSV space rather than matching palette ids.
            /// </summary>
            public Color ExampleColor { get; }

            /// <summary>First palette index this slot recolours.</summary>
            public int Offset { get; }

            /// <summary>Number of palette indices this slot recolours.</summary>
            public int Length { get; }
        }
    }
}

namespace uTank2
{
    /// <summary>A spell present on an item.</summary>
    public sealed class MySpell
    {
        public MySpell(int id, string name)
        {
            Id = id;
            Name = name ?? string.Empty;
        }

        public int Id { get; }

        public string Name { get; }
    }
}
