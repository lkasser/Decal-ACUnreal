using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;

namespace AC.Dat
{
    /// <summary>
    /// The client data the host needs: spell names, skill formulas, palette colours.
    /// </summary>
    /// <remarks>
    /// The protocol sends ids where a player sees words. A spell on an item is a
    /// number, a palette slot is a number, and a skill's contribution from attributes
    /// is a formula that lives only here. Without this file a loot rule on a spell's
    /// name or an item's colour can never match, because the host has nothing to
    /// compare against.
    ///
    /// The two small tables are read once at open. Palettes are read on demand and
    /// cached, because there are thousands and a session touches few.
    /// </remarks>
    public sealed class PortalData : IDisposable
    {
        private const uint PaletteFileType = 0x04000000;

        private readonly DatDatabase _dat;
        private readonly ConcurrentDictionary<uint, IReadOnlyList<uint>> _palettes = new ConcurrentDictionary<uint, IReadOnlyList<uint>>();

        private PortalData(DatDatabase dat, IReadOnlyDictionary<uint, SpellInfo> spells, IReadOnlyDictionary<uint, SkillInfo> skills)
        {
            _dat = dat;
            Spells = spells;
            Skills = skills;
        }

        public IReadOnlyDictionary<uint, SpellInfo> Spells { get; }

        public IReadOnlyDictionary<uint, SkillInfo> Skills { get; }

        /// <summary>The spell components, by the ids spells list them under.</summary>
        public IReadOnlyDictionary<uint, SpellComponentInfo> Components { get; private set; } = new Dictionary<uint, SpellComponentInfo>();

        /// <summary>How maximum health (1), stamina (3) and mana (5) derive from attributes.</summary>
        public IReadOnlyDictionary<uint, SkillFormulaInfo> VitalFormulas { get; private set; } = new Dictionary<uint, SkillFormulaInfo>();

        public string Path => _dat.Path;

        public int FileCount => _dat.FileCount;

        /// <summary>
        /// Opens <c>client_portal.dat</c> and reads the spell and skill tables.
        /// </summary>
        public static PortalData Open(string portalDatPath)
        {
            DatDatabase dat = DatDatabase.Open(portalDatPath);

            try
            {
                byte[] spellData = dat.Read(SpellTable.FileId);
                byte[] skillData = dat.Read(SkillTable.FileId);

                IReadOnlyDictionary<uint, SpellInfo> spells = spellData != null
                    ? SpellTable.Parse(spellData)
                    : new Dictionary<uint, SpellInfo>();

                IReadOnlyDictionary<uint, SkillInfo> skills = skillData != null
                    ? SkillTable.Parse(skillData)
                    : new Dictionary<uint, SkillInfo>();

                byte[] componentData = dat.Read(SpellComponentTable.FileId);
                byte[] vitalData = dat.Read(VitalTable.FileId);

                return new PortalData(dat, spells, skills)
                {
                    Components = componentData != null
                        ? SpellComponentTable.Parse(componentData)
                        : new Dictionary<uint, SpellComponentInfo>(),
                    VitalFormulas = vitalData != null
                        ? VitalTable.Parse(vitalData)
                        : new Dictionary<uint, SkillFormulaInfo>(),
                };
            }
            catch
            {
                dat.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Finds the client's portal DAT in the usual places, or returns null.
        /// </summary>
        /// <remarks>
        /// Checked in order of how likely each is to be the set the client is
        /// actually running against.
        /// </remarks>
        public static string FindPortalDat(params string[] extraDirectories)
        {
            List<string> candidates = new List<string>();

            if (extraDirectories != null)
                candidates.AddRange(extraDirectories);

            candidates.Add(@"C:\ACE\Dats");
            candidates.Add(@"C:\Turbine\Asheron's Call");
            candidates.Add(@"C:\Games\Turbine\Asheron's Call");

            foreach (string directory in candidates)
            {
                if (string.IsNullOrWhiteSpace(directory))
                    continue;

                string path = System.IO.Path.Combine(directory, "client_portal.dat");

                if (File.Exists(path))
                    return path;
            }

            return null;
        }

        public string GetSpellName(uint spellId)
            => Spells.TryGetValue(spellId, out SpellInfo spell) ? spell.Name : null;

        public SpellInfo GetSpell(uint spellId)
            => Spells.TryGetValue(spellId, out SpellInfo spell) ? spell : null;

        public SpellComponentInfo GetComponent(uint componentId)
            => Components.TryGetValue(componentId, out SpellComponentInfo component) ? component : null;

        /// <summary>How a maximum vital (1 health, 3 stamina, 5 mana) derives from attributes.</summary>
        public bool TryGetVitalFormula(uint vitalId, out SkillFormulaInfo formula)
            => VitalFormulas.TryGetValue(vitalId, out formula);

        public bool TryGetSkillFormula(uint skillId, out SkillFormulaInfo formula)
        {
            if (Skills.TryGetValue(skillId, out SkillInfo skill))
            {
                formula = skill.Formula;
                return true;
            }

            formula = default;
            return false;
        }

        /// <summary>
        /// A representative colour for one of an object's palette slots.
        /// </summary>
        /// <param name="paletteId">The slot's palette, as sent in the object's model data.</param>
        /// <param name="offset">The slot's offset field, in units of eight colours.</param>
        /// <param name="length">The slot's length field, in units of eight colours; zero means the whole palette.</param>
        /// <remarks>
        /// A slot covers a run of colours, not one. The middle of the run is sampled,
        /// which is a choice: it is stable and avoids the near-black and near-white
        /// ends that shading ramps tend to have, but it is not known to be the same
        /// sample the retail plugins took. Colour rules will therefore behave
        /// consistently here without necessarily agreeing with a profile tuned
        /// against retail.
        /// </remarks>
        public Color? GetSlotColor(uint paletteId, int offset, int length)
        {
            IReadOnlyList<uint> colors = GetPalette(paletteId);

            if (colors.Count == 0)
                return null;

            int start = offset * 8;
            int run = length == 0 ? 256 * 8 : length * 8;

            if (start >= colors.Count)
                return null;

            int end = Math.Min(start + run, colors.Count);
            int sample = start + ((end - start) / 2);

            if (sample >= colors.Count)
                sample = colors.Count - 1;

            uint argb = colors[sample];

            return Color.FromArgb(
                (int)((argb >> 24) & 0xFF),
                (int)((argb >> 16) & 0xFF),
                (int)((argb >> 8) & 0xFF),
                (int)(argb & 0xFF));
        }

        public IReadOnlyList<uint> GetPalette(uint paletteId)
        {
            if (paletteId == 0)
                return Array.Empty<uint>();

            // Slots sometimes carry the id without its type byte.
            uint fileId = (paletteId & 0xFF000000) == 0 ? paletteId | PaletteFileType : paletteId;

            return _palettes.GetOrAdd(fileId, id =>
            {
                byte[] data = _dat.Read(id);
                return data != null ? PaletteFile.Parse(data) : Array.Empty<uint>();
            });
        }

        /// <summary>
        /// Decodes one of the client's images - an icon, or a piece of the interface art the
        /// Decal look is built from. A bare number is taken as the low part of a 0x06 id, the
        /// way Decal's view files write them.
        /// </summary>
        public bool TryGetImage(uint imageId, out RgbaImage image, out string error)
        {
            uint fileId = (imageId & 0xFF000000) == 0 ? imageId | ImageFileType : imageId;

            byte[] data = _dat.Read(fileId);
            if (data == null)
            {
                image = null;
                error = $"0x{fileId:X8} is not in {System.IO.Path.GetFileName(Path)}";
                return false;
            }

            return DatImage.TryDecode(data, GetPalette, out image, out error);
        }

        /// <summary>The raw file, for reporting what an image's header says.</summary>
        public byte[] ReadFile(uint fileId) => _dat.Read(fileId);

        private const uint ImageFileType = 0x06000000;

        public void Dispose() => _dat.Dispose();
    }
}
