using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace VirindiHotkeySystem
{
    /// <summary>
    /// Virindi Hotkey System, as Decal plugins reach it: the hotkeys they add, for the host to list
    /// in its own hotkey windows and to fire when their keys are pressed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The real VHS was a Decal plugin of its own, with a window where the player bound each
    /// plugin's hotkeys, and a keyboard hook in the client. This host does that job itself
    /// (Decal's window, its hotkey windows), so the real VHS is never loaded; a plugin built
    /// against it is given this instead, and finds it running. What a plugin adds here the
    /// compatibility plugin offers the host as hotkeys of its own, under the plugin's name, and
    /// a key the player presses for one raises its <see cref="VHotkeyInfo.Fired2"/>.
    /// </para>
    /// <para>
    /// Mag-Tools adds four at login - Pack Inventory on Ctrl+P, One Touch Heal, and Maximize and
    /// Minimize Chat - and without an assembly by this name its whole login handler failed.
    /// Choosing a key from inside a plugin (<see cref="BeginUserSelect"/>) is the host's hotkey
    /// window's to do, and does nothing here.
    /// </para>
    /// </remarks>
    public class VHotkeySystem
    {
        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete]
        public delegate void delHotkeyHit(VHotkeyInfo key, ref bool Eat);

        private static readonly VHotkeySystem Real = new VHotkeySystem();

        [Obsolete]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public static VHotkeySystem Instance = Real;

        private readonly List<VHotkeyInfo> _hotkeys = new List<VHotkeyInfo>();
        private readonly object _gate = new object();

        /// <summary>The one hotkey system, which is always running where a plugin can reach it.</summary>
        public static VHotkeySystem InstanceReal => Real;

        public static bool Running => true;

        /// <summary>Every hotkey added, in the order added.</summary>
        public ReadOnlyCollection<VHotkeyInfo> AllHotkeys
        {
            get
            {
                lock (_gate)
                    return _hotkeys.ToList().AsReadOnly();
            }
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("Global hotkey events are deprecated. Use the event on the VHotkeyInfo class.")]
        public event delHotkeyHit HotkeyHit;

        /// <summary>A hotkey was added or taken away, or one changed.</summary>
        public event EventHandler HotkeyListChanged;

        /// <summary>
        /// Adds a hotkey, or replaces one of the same owner and name - as VHS did, so a plugin that
        /// adds its hotkeys at every login has each once.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void AddHotkey(VHotkeyInfo hk)
        {
            if (hk == null)
                throw new ArgumentNullException(nameof(hk));

            hk.Owner ??= Assembly.GetCallingAssembly();
            lock (_gate)
            {
                _hotkeys.RemoveAll(h => h != hk && h.Key == hk.Key);
                if (!_hotkeys.Contains(hk))
                    _hotkeys.Add(hk);
            }

            Changed();
        }

        public void RemoveHotkey(VHotkeyInfo hk)
        {
            bool removed;
            lock (_gate)
                removed = _hotkeys.Remove(hk);

            if (removed)
                Changed();
        }

        /// <summary>Takes away every hotkey the calling plugin's assembly added under its own name.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ClearMyHotkeys() => ClearMyHotkeys(Assembly.GetCallingAssembly().GetName().Name);

        /// <summary>Takes away every hotkey added under an owner's name.</summary>
        public void ClearMyHotkeys(string asm)
        {
            int removed;
            lock (_gate)
                removed = _hotkeys.RemoveAll(h => string.Equals(h.AssemblyName, asm, StringComparison.Ordinal));

            if (removed > 0)
                Changed();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public VHotkeyInfo GetHotkeyByName(string nm) => GetHotkeyByName(Assembly.GetCallingAssembly().GetName().Name, nm);

        public VHotkeyInfo GetHotkeyByName(string asm, string nm)
        {
            lock (_gate)
                return _hotkeys.FirstOrDefault(h => string.Equals(h.AssemblyName, asm, StringComparison.Ordinal) && string.Equals(h.HotkeyName, nm, StringComparison.Ordinal));
        }

        /// <summary>VHS's window for choosing a hotkey's key. The host's hotkey windows choose keys; nothing here.</summary>
        public void BeginUserSelect(VHotkeyInfo k)
        {
        }

        public void CancelUserSelect()
        {
        }

        /// <summary>
        /// The key of a hotkey was pressed: the old global event, then the hotkey's own handlers.
        /// True when the key is eaten.
        /// </summary>
        internal bool Press(VHotkeyInfo hk)
        {
#pragma warning disable CS0612, CS0618
            bool eat = false;
            HotkeyHit?.Invoke(hk, ref eat);
#pragma warning restore CS0612, CS0618
            return hk.Raise() || eat;
        }

        /// <summary>
        /// Takes away every hotkey a plugin's assemblies added, and its handlers of the system's own
        /// events, for one that has been stopped: nothing here may keep its code loaded.
        /// </summary>
        internal void Release(Func<Assembly, bool> theirs)
        {
            int removed;
            lock (_gate)
                removed = _hotkeys.RemoveAll(h => h.Owner != null && theirs(h.Owner));

            HotkeyListChanged = Without(HotkeyListChanged, theirs);
#pragma warning disable CS0612, CS0618
            HotkeyHit = Without(HotkeyHit, theirs);
#pragma warning restore CS0612, CS0618

            if (removed > 0)
                Changed();
        }

        private static T Without<T>(T handler, Func<Assembly, bool> theirs) where T : Delegate
        {
            Delegate kept = null;
            foreach (Delegate subscriber in handler?.GetInvocationList() ?? Array.Empty<Delegate>())
            {
                if (!theirs(subscriber.Method.Module.Assembly) && (subscriber.Target == null || !theirs(subscriber.Target.GetType().Assembly)))
                    kept = Delegate.Combine(kept, subscriber);
            }

            return (T)kept;
        }

        internal bool Holds(VHotkeyInfo hk)
        {
            lock (_gate)
                return _hotkeys.Contains(hk);
        }

        internal void OnChanged(VHotkeyInfo hk)
        {
            if (Holds(hk))
                Changed();
        }

        private void Changed() => HotkeyListChanged?.Invoke(this, EventArgs.Empty);
    }
}
