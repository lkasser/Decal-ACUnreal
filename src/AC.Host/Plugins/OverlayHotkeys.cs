using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AC.Host.Plugins
{
    /// <summary>
    /// A plugin with actions a key can trigger - Virindi Tank's "run the macro", say - as
    /// Virindi's hotkey system gave Decal plugins. The plugin names the actions and suggests a
    /// key for each; the player can bind them differently in Decal's window. A bound key
    /// pressed in the game is told to the plugin and kept from the game.
    /// </summary>
    /// <remarks>
    /// Optional: a plugin without it simply has no hotkeys, and an older host that does not
    /// know it never asks.
    /// </remarks>
    public interface IOverlayHotkeys
    {
        /// <summary>The actions a key can trigger. Read on the game thread, often; keep it cheap.</summary>
        IReadOnlyList<HotkeyDefinition> Hotkeys { get; }

        /// <summary>A key bound to one of <see cref="Hotkeys"/> was pressed. Game thread.</summary>
        void HotkeyPressed(string id);
    }

    /// <summary>One action a key can trigger.</summary>
    public sealed class HotkeyDefinition
    {
        public HotkeyDefinition(string id, string description, string defaultKeys = null, string name = null, string source = null)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A hotkey needs an id.", nameof(id));
            Id = id;
            Description = string.IsNullOrWhiteSpace(description) ? id : description;
            DefaultKeys = defaultKeys ?? string.Empty;
            Name = string.IsNullOrWhiteSpace(name) ? id : name;
            Source = string.IsNullOrWhiteSpace(source) ? null : source;
        }

        /// <summary>
        /// The plugin a hotkey window names beside it, when that is not the plugin giving it - the
        /// Virindi HUDs' "VHUDs" for the HUDs Virindi Tank hosts, say. Null for the plugin's own.
        /// </summary>
        public string Source { get; }

        /// <summary>
        /// Its short name, as a hotkey window lists it beside the description - Virindi Tank's
        /// "Start/Stop". The id unless the plugin gives one.
        /// </summary>
        public string Name { get; }

        /// <summary>What the plugin calls it; comes back in <see cref="IOverlayHotkeys.HotkeyPressed"/>.</summary>
        public string Id { get; }

        /// <summary>What Decal's window calls it.</summary>
        public string Description { get; }

        /// <summary>The key suggested, as "Ctrl+F12"; empty for none until the player binds one.</summary>
        public string DefaultKeys { get; }
    }

    /// <summary>A key with the modifiers held with it: "Ctrl+Shift+F12".</summary>
    public readonly struct KeyChord : IEquatable<KeyChord>
    {
        public KeyChord(int key, bool ctrl = false, bool shift = false, bool alt = false)
        {
            Key = key;
            Ctrl = ctrl;
            Shift = shift;
            Alt = alt;
        }

        /// <summary>The virtual-key code.</summary>
        public int Key { get; }

        public bool Ctrl { get; }

        public bool Shift { get; }

        public bool Alt { get; }

        public bool IsEmpty => Key == 0;

        private static readonly Dictionary<string, int> Names = BuildNames();

        private static Dictionary<string, int> BuildNames()
        {
            Dictionary<string, int> names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["Space"] = 0x20, ["Tab"] = 0x09, ["Enter"] = 0x0D, ["Esc"] = 0x1B, ["Escape"] = 0x1B,
                ["Backspace"] = 0x08, ["Insert"] = 0x2D, ["Delete"] = 0x2E, ["Home"] = 0x24, ["End"] = 0x23,
                ["PageUp"] = 0x21, ["PageDown"] = 0x22, ["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28,
                ["Pause"] = 0x13, ["ScrollLock"] = 0x91,
                ["`"] = 0xC0, ["-"] = 0xBD, ["="] = 0xBB, ["["] = 0xDB, ["]"] = 0xDD, ["\\"] = 0xDC,
                [";"] = 0xBA, ["'"] = 0xDE, [","] = 0xBC, ["."] = 0xBE, ["/"] = 0xBF,
            };

            for (int f = 1; f <= 24; f++)
                names["F" + f.ToString(CultureInfo.InvariantCulture)] = 0x70 + f - 1;
            for (int n = 0; n <= 9; n++)
                names["Numpad" + n.ToString(CultureInfo.InvariantCulture)] = 0x60 + n;
            for (char c = 'A'; c <= 'Z'; c++)
                names[c.ToString()] = c;
            for (char c = '0'; c <= '9'; c++)
                names[c.ToString()] = c;

            return names;
        }

        /// <summary>Reads "Ctrl+Shift+F12", "Alt+1", "F5". Empty text is no key, which parses.</summary>
        public static bool TryParse(string text, out KeyChord chord)
        {
            chord = default;
            if (string.IsNullOrWhiteSpace(text))
                return true;

            bool ctrl = false, shift = false, alt = false;
            int key = 0;

            string[] parts = text.Split('+', StringSplitOptions.TrimEntries);

            // "Ctrl++" names the plus key itself; a trailing empty part is it.
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                if (part.Length == 0)
                {
                    if (i == parts.Length - 1 && key == 0)
                    {
                        key = 0xBB;
                        continue;
                    }

                    continue;
                }

                switch (part.ToUpperInvariant())
                {
                    case "CTRL":
                    case "CONTROL":
                        ctrl = true;
                        continue;
                    case "SHIFT":
                        shift = true;
                        continue;
                    case "ALT":
                        alt = true;
                        continue;
                }

                if (key != 0 || !Names.TryGetValue(part, out key))
                    return false;
            }

            if (key == 0)
                return false;

            chord = new KeyChord(key, ctrl, shift, alt);
            return true;
        }

        public override string ToString()
        {
            if (IsEmpty)
                return string.Empty;

            int key = Key;
            string name = Names.Where(p => p.Value == key && p.Key.Length > 0)
                .Select(p => p.Key)
                .OrderBy(n => n.Equals("Escape", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .FirstOrDefault() ?? key.ToString(CultureInfo.InvariantCulture);

            return (Ctrl ? "Ctrl+" : string.Empty) + (Shift ? "Shift+" : string.Empty) + (Alt ? "Alt+" : string.Empty) + name;
        }

        public bool Equals(KeyChord other) => Key == other.Key && Ctrl == other.Ctrl && Shift == other.Shift && Alt == other.Alt;

        public override bool Equals(object obj) => obj is KeyChord other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Key, Ctrl, Shift, Alt);
    }

    /// <summary>One hotkey as the host has resolved it: whose, which action, and the key bound now.</summary>
    public sealed class BoundHotkey
    {
        internal BoundHotkey(string owner, HotkeyDefinition definition, KeyChord keys)
        {
            Owner = owner;
            Definition = definition;
            Keys = keys;
        }

        public string Owner { get; }

        public HotkeyDefinition Definition { get; }

        /// <summary>Empty when the action has no key.</summary>
        public KeyChord Keys { get; }

        /// <summary>How a binding is filed: "owner/id".</summary>
        public string Name => Owner + "/" + Definition.Id;
    }
}
