using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AC.Dat;
using AC.Host.World;
using Decal.Adapter;
using Decal.Adapter.Hosting;

namespace Decal.Filters
{
    /// <summary>
    /// Decal's FileService: the client's portal file, and the spell and skill tables in it,
    /// for plugins that want a spell's name, school or icon.
    /// </summary>
    /// <remarks>
    /// Read with the host's own reader of client_portal.dat - the same file the host was
    /// started with, when it was, or the one found where the client is installed otherwise.
    /// Without one, every table is empty and every lookup finds nothing, which plugins
    /// already had to cope with while Decal was still reading.
    ///
    /// <para>
    /// The tables the host's reader does not parse - components, levels, heritages and the
    /// like - are here and empty, so a plugin that reaches for one gets nothing rather than
    /// failing to load. The cell file is not read at all.
    /// </para>
    /// </remarks>
    public sealed class FileService : FilterBase
    {
        private PortalData _portal;
        private SpellTable _spells;
        private SkillTable _skills;

        public FileService()
        {
        }

        public override string ReferenceName => "Decal.FileService";

        public SpellTable SpellTable => _spells ??= new SpellTable(Portal);

        public SkillTable SkillTable => _skills ??= new SkillTable(Portal);

        public AttributeTable AttributeTable { get; } = new AttributeTable();

        public VitalTable VitalTable { get; } = new VitalTable();

        public ComponentTable ComponentTable { get; } = new ComponentTable();

        public GenderTable GenderTable { get; } = new GenderTable();

        public HeritageTable HeritageTable { get; } = new HeritageTable();

        public SpeciesTable SpeciesTable { get; } = new SpeciesTable();

        public MaterialTable MaterialTable { get; } = new MaterialTable();

#pragma warning disable CS0067
        public event EventHandler<UpdateEventArgs> OnUpdatePortal;

        public event EventHandler<UpdateEventArgs> OnUpdateCell;
#pragma warning restore CS0067

        /// <summary>A portal file's bytes, or null when there is no portal file or no such entry.</summary>
        public byte[] GetPortalFile(int fileId)
        {
            try
            {
                return Portal?.ReadFile(unchecked((uint)fileId));
            }
            catch (Exception ex) when (ex is IOException || ex is KeyNotFoundException || ex is InvalidDataException)
            {
                return null;
            }
        }

        /// <summary>A cell file's bytes. The host does not read the cell file, so null.</summary>
        public byte[] GetCellFile(int fileId) => null;

        protected override void Startup()
        {
        }

        protected override void Shutdown()
        {
            _portal?.Dispose();
            _portal = null;
        }

        /// <summary>The portal file, opened on first use: the host's, or the client's where it is installed.</summary>
        private PortalData Portal
        {
            get
            {
                if (_portal != null)
                    return _portal;

                string path = (DecalRuntime.Current?.Host.GameData as PortalGameData)?.Path ?? PortalData.FindPortalDat();
                if (path == null)
                    return null;

                try
                {
                    _portal = PortalData.Open(path);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
                {
                    DecalRuntime.Current?.Host.Log.Warn($"[Decal] FileService could not read {path}: {ex.Message}. Its tables are empty.");
                }

                return _portal;
            }
        }
    }

    public class UpdateEventArgs : EventArgs
    {
        public int FileId { get; set; }
    }

    /// <summary>What every table entry has: an id and a name.</summary>
    public interface IIdNameTableEntry
    {
        int Id { get; }

        string Name { get; }
    }

    /// <summary>A table of entries found by id or by name, as Decal's tables were.</summary>
    public abstract class IdNameTable<EntryType>
        where EntryType : class, IIdNameTableEntry
    {
        private readonly List<EntryType> _entries = new List<EntryType>();
        private readonly Dictionary<int, int> _byId = new Dictionary<int, int>();
        private readonly Dictionary<string, int> _byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        protected IdNameTable()
        {
        }

#pragma warning disable CS0067
        public event EventHandler Update;
#pragma warning restore CS0067

        public EntryType this[int index] => _entries[index];

        public int Length => _entries.Count;

        public int IndexFromId(int id) => _byId.TryGetValue(id, out int index) ? index : -1;

        public int IndexFromName(string name) => name != null && _byName.TryGetValue(name, out int index) ? index : -1;

        public EntryType GetById(int id) => _byId.TryGetValue(id, out int index) ? _entries[index] : null;

        public EntryType GetByName(string name) => name != null && _byName.TryGetValue(name, out int index) ? _entries[index] : null;

        protected void InitializeTable(int n) => _entries.Capacity = Math.Max(_entries.Capacity, n);

        protected void Add(EntryType entry)
        {
            _byId[entry.Id] = _entries.Count;
            if (!string.IsNullOrEmpty(entry.Name))
                _byName.TryAdd(entry.Name, _entries.Count);

            _entries.Add(entry);
        }
    }

    /// <summary>A plain table of values by index.</summary>
    public abstract class RawTable<EntryType>
    {
        private readonly List<EntryType> _entries = new List<EntryType>();

        protected RawTable()
        {
        }

#pragma warning disable CS0067
        public event EventHandler Update;
#pragma warning restore CS0067

        public EntryType this[int index]
        {
            get => _entries[index];
            set => _entries[index] = value;
        }

        public int Length => _entries.Count;

        protected void InitializeTable(int n) => _entries.Capacity = Math.Max(_entries.Capacity, n);

        protected void Add(EntryType entry) => _entries.Add(entry);
    }

    /// <summary>A school of magic: war, life, item and creature enchantment, void.</summary>
    public sealed class SpellSchool
    {
        private static readonly SpellSchool[] Schools =
        {
            new SpellSchool(0, "None"),
            new SpellSchool(1, "War Magic"),
            new SpellSchool(2, "Life Magic"),
            new SpellSchool(3, "Item Enchantment"),
            new SpellSchool(4, "Creature Enchantment"),
            new SpellSchool(5, "Void Magic"),
        };

        private SpellSchool(int id, string name)
        {
            Id = id;
            Name = name;
        }

        public int Id { get; }

        public string Name { get; }

        public static SpellSchool GetById(int id) => id >= 0 && id < Schools.Length ? Schools[id] : Schools[0];

        public static SpellSchool GetByName(string name) => Schools.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

        public override string ToString() => Name;
    }

    /// <summary>
    /// A spell, as the portal file describes it. What the host's reader keeps - name,
    /// description, school, icon, family and difficulty - is filled in; the rest of Decal's
    /// fields read as zero.
    /// </summary>
    public class Spell : IIdNameTableEntry
    {
        internal Spell(SpellInfo info)
        {
            Id = unchecked((int)info.Id);
            Name = info.Name ?? string.Empty;
            Description = info.Description ?? string.Empty;
            School = SpellSchool.GetById(unchecked((int)info.School));
            IconId = unchecked((int)info.Icon);
            Family = unchecked((int)info.Category);
            Difficulty = unchecked((int)info.Power);
        }

        public int Id { get; }

        public string Name { get; set; }

        public string Description { get; set; }

        public int IconId { get; set; }

        public int Family { get; set; }

        public int Type { get; set; }

        public int Flags { get; set; }

        public SpellSchool School { get; set; }

        public int Difficulty { get; set; }

        public int Mana { get; set; }

        public SpellComponentIDs ComponentIDs { get; set; } = new SpellComponentIDs();

        public float Speed { get; set; }

        public double Duration { get; set; }

        public int CasterEffect { get; set; }

        public int TargetEffect { get; set; }

        public int TargetMask { get; set; }

        public int Generation { get; set; }

        public int SortKey { get; set; }

        // Fields Decal read from the spell record without knowing what they were.
        public float Unknown1 { get; set; }

        public float Unknown2 { get; set; }

        public float Unknown3 { get; set; }

        public int Unknown4 { get; set; }

        public double Unknown5 { get; set; }

        public int Unknown6 { get; set; }

        public int Unknown7 { get; set; }

        public int Unknown8 { get; set; }

        public int Unknown9 { get; set; }

        public int Unknown10 { get; set; }

        // The spell's flag word is not kept by the host's reader, so none of these can be told;
        // false is what a plugin sees for a spell it knows nothing special about.
        public bool IsOffensive => false;

        public bool IsIrresistible => false;

        public bool IsUntargetted => false;

        public bool IsDebuff => false;

        public bool IsFellowship => false;

        public bool IsFastWindup => false;

        public override string ToString() => Name;
    }

    public sealed class SpellTable : IdNameTable<Spell>
    {
        internal SpellTable(PortalData portal)
        {
            if (portal == null)
                return;

            InitializeTable(portal.Spells.Count);
            foreach (SpellInfo info in portal.Spells.Values.OrderBy(s => s.Id))
                Add(new Spell(info));
        }
    }

    public sealed class SpellComponentIDs : RawTable<int>
    {
        internal SpellComponentIDs()
        {
        }
    }

    public class SkillType
    {
        private SkillType(int id, string name)
        {
            Id = id;
            Name = name;
        }

        public int Id { get; }

        public string Name { get; }

        public static SkillType GetById(int id) => new SkillType(id, id.ToString(System.Globalization.CultureInfo.InvariantCulture));

        public static SkillType GetByName(string name) => null;

        public override string ToString() => Name;
    }

    public class SkillState
    {
        private SkillState(int id, string name)
        {
            Id = id;
            Name = name;
        }

        public int Id { get; }

        public string Name { get; }

        public static SkillState GetById(int id) => new SkillState(id, id.ToString(System.Globalization.CultureInfo.InvariantCulture));

        public static SkillState GetByName(string name) => null;

        public override string ToString() => Name;
    }

    /// <summary>A skill, as the portal file describes it: its name, description and formula.</summary>
    public class Skill : IIdNameTableEntry
    {
        internal Skill(SkillInfo info)
        {
            Id = unchecked((int)info.Id);
            Name = info.Name ?? string.Empty;
            Description = info.Description ?? string.Empty;
            Attribute1Id = unchecked((int)info.Formula.Attribute1);
            Attribute2Id = unchecked((int)info.Formula.Attribute2);
            AttributeDivisor = unchecked((int)info.Formula.Divisor);
            IsAttribute1Valid = Attribute1Id != 0;
            IsAttribute2Valid = Attribute2Id != 0;
        }

        public int Id { get; }

        public string Name { get; set; }

        public string Description { get; set; }

        public int IconId { get; set; }

        public int CreditsToTrain { get; set; }

        public int CreditsToSpecialize { get; set; }

        public SkillType Type { get; set; }

        public SkillState Usability { get; set; }

        public bool IsAttribute1Valid { get; set; }

        public bool IsAttribute2Valid { get; set; }

        public int Attribute1Id { get; set; }

        public int Attribute2Id { get; set; }

        public int AttributeDivisor { get; set; }

        public double XPTimerStart { get; set; }

        public double XPTimerLimit { get; set; }

        public override string ToString() => Name;
    }

    public sealed class SkillTable : IdNameTable<Skill>
    {
        internal SkillTable(PortalData portal)
        {
            if (portal == null)
                return;

            InitializeTable(portal.Skills.Count);
            foreach (SkillInfo info in portal.Skills.Values.OrderBy(s => s.Id))
                Add(new Skill(info));
        }
    }

    /// <summary>An entry in one of the tables the host does not read: only ever seen empty.</summary>
    public class Attrib : IIdNameTableEntry
    {
        internal Attrib()
        {
        }

        public int Id => 0;

        public string Name { get; set; } = string.Empty;

        public int IconId { get; set; }
    }

    public sealed class AttributeTable : IdNameTable<Attrib>
    {
        internal AttributeTable()
        {
        }
    }

    public class Vital : IIdNameTableEntry
    {
        internal Vital()
        {
        }

        public int Id => 0;

        public string Name { get; set; } = string.Empty;

        public int IconId { get; set; }
    }

    public sealed class VitalTable : IdNameTable<Vital>
    {
        internal VitalTable()
        {
        }
    }

    /// <summary>
    /// What kind of spell component one is - scarab, herb, taper - as Decal's FileService
    /// named the seven. A class with one instance per kind, as Decal's was, not an enum.
    /// </summary>
    public class ComponentType
    {
        private static readonly ComponentType[] Types =
        {
            new ComponentType(0, "Scarab"),
            new ComponentType(1, "Herb"),
            new ComponentType(2, "Powder"),
            new ComponentType(3, "Potion"),
            new ComponentType(4, "Talisman"),
            new ComponentType(5, "Taper"),
            new ComponentType(6, "Pea"),
        };

        private ComponentType(int id, string name)
        {
            Id = id;
            Name = name;
        }

        public int Id { get; }

        public string Name { get; }

        /// <summary>The kind with this number, or null.</summary>
        public static ComponentType GetById(int id) => Array.Find(Types, t => t.Id == id);

        /// <summary>The kind with this name, ignoring case, or null.</summary>
        public static ComponentType GetByName(string name)
            => Array.Find(Types, t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

        public override string ToString() => Name;
    }

    public class Component : IIdNameTableEntry
    {
        internal Component()
        {
        }

        public int Id => 0;

        public string Name { get; set; } = string.Empty;

        /// <summary>Its kind; null until the component table is read, which it is not yet.</summary>
        public ComponentType Type { get; set; }

        public int IconId { get; set; }

        public int SortKey { get; set; }

        public string Word { get; set; } = string.Empty;
    }

    public sealed class ComponentTable : IdNameTable<Component>
    {
        internal ComponentTable()
        {
        }
    }

    public class Gender : IIdNameTableEntry
    {
        internal Gender()
        {
        }

        public int Id => 0;

        public string Name { get; set; } = string.Empty;
    }

    public sealed class GenderTable : IdNameTable<Gender>
    {
        internal GenderTable()
        {
        }
    }

    public class Heritage : IIdNameTableEntry
    {
        internal Heritage()
        {
        }

        public int Id => 0;

        public string Name { get; set; } = string.Empty;
    }

    public sealed class HeritageTable : IdNameTable<Heritage>
    {
        internal HeritageTable()
        {
        }
    }

    public class Species : IIdNameTableEntry
    {
        internal Species()
        {
        }

        public int Id => 0;

        public string Name { get; set; } = string.Empty;
    }

    public sealed class SpeciesTable : IdNameTable<Species>
    {
        internal SpeciesTable()
        {
        }
    }

    public class Material : IIdNameTableEntry
    {
        internal Material()
        {
        }

        public int Id => 0;

        public string Name { get; set; } = string.Empty;
    }

    public sealed class MaterialTable : IdNameTable<Material>
    {
        internal MaterialTable()
        {
        }
    }
}
