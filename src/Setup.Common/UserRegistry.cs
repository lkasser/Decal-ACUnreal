using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;

namespace Setup.Common
{
    /// <summary>
    /// The part of the registry the setups write: keys under HKEY_CURRENT_USER, named relative
    /// to it. There is no way here to name another hive, so nothing a setup does can reach
    /// HKEY_LOCAL_MACHINE - least of all Decal's own keys there.
    /// </summary>
    /// <remarks>An interface so that tests, and a run with /NoRegistry, write to memory instead.</remarks>
    public interface IUserRegistry
    {
        /// <summary>A value, or null when the key or the value is not there.</summary>
        object GetValue(string key, string name);

        /// <summary>Writes a string, or an int as a DWORD, creating the key if need be.</summary>
        void SetValue(string key, string name, object value);

        /// <summary>Deletes a key and everything under it. Harmless when it is not there.</summary>
        void DeleteKey(string key);

        bool KeyExists(string key);
    }

    /// <summary>HKEY_CURRENT_USER itself.</summary>
    public sealed class CurrentUserRegistry : IUserRegistry
    {
        public object GetValue(string key, string name)
        {
            using RegistryKey opened = Registry.CurrentUser.OpenSubKey(key);
            return opened?.GetValue(name);
        }

        public void SetValue(string key, string name, object value)
        {
            using RegistryKey opened = Registry.CurrentUser.CreateSubKey(key, writable: true);
            switch (value)
            {
                case int number:
                    opened.SetValue(name, number, RegistryValueKind.DWord);
                    break;
                case string text:
                    opened.SetValue(name, text, RegistryValueKind.String);
                    break;
                default:
                    throw new ArgumentException($"A registry value is a string or an int, not {value?.GetType().Name ?? "null"}.", nameof(value));
            }
        }

        public void DeleteKey(string key) => Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);

        public bool KeyExists(string key)
        {
            using RegistryKey opened = Registry.CurrentUser.OpenSubKey(key);
            return opened != null;
        }
    }

    /// <summary>
    /// A registry held in memory: what tests check, and what a /NoRegistry run writes to and
    /// forgets, so that a trial install on a developer's machine leaves the real one alone.
    /// </summary>
    public sealed class MemoryRegistry : IUserRegistry
    {
        private readonly Dictionary<string, Dictionary<string, object>> _keys = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Every key written, for tests to look through.</summary>
        public IReadOnlyCollection<string> Keys => _keys.Keys;

        public object GetValue(string key, string name)
            => _keys.TryGetValue(Trim(key), out Dictionary<string, object> values) && values.TryGetValue(name ?? string.Empty, out object value) ? value : null;

        public void SetValue(string key, string name, object value)
        {
            if (value is not int && value is not string)
                throw new ArgumentException($"A registry value is a string or an int, not {value?.GetType().Name ?? "null"}.", nameof(value));

            string trimmed = Trim(key);
            if (!_keys.TryGetValue(trimmed, out Dictionary<string, object> values))
                _keys[trimmed] = values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            values[name ?? string.Empty] = value;
        }

        public void DeleteKey(string key)
        {
            string trimmed = Trim(key);
            foreach (string existing in _keys.Keys.ToList())
            {
                if (string.Equals(existing, trimmed, StringComparison.OrdinalIgnoreCase)
                    || existing.StartsWith(trimmed + "\\", StringComparison.OrdinalIgnoreCase))
                    _keys.Remove(existing);
            }
        }

        public bool KeyExists(string key) => _keys.ContainsKey(Trim(key));

        private static string Trim(string key) => (key ?? string.Empty).Trim('\\');
    }
}
