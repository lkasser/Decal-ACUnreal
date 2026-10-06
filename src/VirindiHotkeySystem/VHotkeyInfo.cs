using System;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace VirindiHotkeySystem
{
    /// <summary>
    /// One action a key can trigger, as a plugin gives it to Virindi Hotkey System: its name, its
    /// description, the key suggested for it, and the event raised when the key is pressed.
    /// </summary>
    /// <remarks>
    /// As VHS's own: a plugin makes one, adds it (<see cref="VHotkeySystem.AddHotkey"/>) and
    /// listens to <see cref="Fired2"/>. Here the hotkey is listed in the host's hotkey windows,
    /// where the player binds it, and a press of its key raises <see cref="Fired2"/>. The
    /// suggested key and modifiers are what it was made with; the player's own binding is the
    /// host's, kept with the host's other hotkeys. VHS's icons are not offered.
    /// </remarks>
    public class VHotkeyInfo
    {
        /// <summary>What a hotkey's handler is told; it eats the key unless it says otherwise.</summary>
        public class cEatableFiredEventArgs : EventArgs
        {
            public bool Eat = true;
        }

        public const int KEYCODE_MOUSE_WHEELUP = -1;

        public const int KEYCODE_MOUSE_WHEELDOWN = -2;

        public const int KEYCODE_MOUSE_WHEELLEFT = -3;

        public const int KEYCODE_MOUSE_WHEELRIGHT = -4;

        public const int KEYCODE_MOUSE_MIDDLEBUTTON = -5;

        public const int KEYCODE_MOUSE_XBUTTON1 = -6;

        public const int KEYCODE_MOUSE_XBUTTON2 = -7;

        private readonly bool _defaultAlt;
        private readonly bool _defaultControl;
        private readonly bool _defaultShift;
        private readonly int _defaultKey;
        private readonly bool _defaultEnabled;
        private bool _alt;
        private bool _control;
        private bool _shift;
        private string _description;
        private bool _enabled;
        private string _name;
        private int _key;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public VHotkeyInfo(string photkeyname, string pdescription, int pvirtualkey, bool paltstate, bool pcontrolstate, bool pshiftstate)
            : this(Assembly.GetCallingAssembly().GetName().Name, true, photkeyname, pdescription, pvirtualkey, paltstate, pcontrolstate, pshiftstate)
        {
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public VHotkeyInfo(bool penabled, string photkeyname, string pdescription, int pvirtualkey, bool paltstate, bool pcontrolstate, bool pshiftstate)
            : this(Assembly.GetCallingAssembly().GetName().Name, penabled, photkeyname, pdescription, pvirtualkey, paltstate, pcontrolstate, pshiftstate)
        {
        }

        /// <param name="asmName">Whose it is: the plugin's assembly, or the name it goes by - Mag-Tools gives "Mag-Tools".</param>
        public VHotkeyInfo(string asmName, bool penabled, string photkeyname, string pdescription, int pvirtualkey, bool paltstate, bool pcontrolstate, bool pshiftstate)
        {
            AssemblyName = asmName ?? string.Empty;
            _enabled = _defaultEnabled = penabled;
            _name = photkeyname ?? string.Empty;
            _description = pdescription ?? string.Empty;
            _key = _defaultKey = pvirtualkey;
            _alt = _defaultAlt = paltstate;
            _control = _defaultControl = pcontrolstate;
            _shift = _defaultShift = pshiftstate;
        }

        /// <summary>Whose it is, as it was made.</summary>
        public string AssemblyName { get; }

        /// <summary>Whether it has been added to the hotkey system.</summary>
        public bool IsInitialized => VHotkeySystem.InstanceReal.Holds(this);

        public bool AltState
        {
            get => _alt;
            set => Change(ref _alt, value);
        }

        public bool ControlState
        {
            get => _control;
            set => Change(ref _control, value);
        }

        public bool ShiftState
        {
            get => _shift;
            set => Change(ref _shift, value);
        }

        public string Description
        {
            get => _description;
            set => Change(ref _description, value ?? string.Empty);
        }

        public bool Enabled
        {
            get => _enabled;
            set => Change(ref _enabled, value);
        }

        public string HotkeyName
        {
            get => _name;
            set => Change(ref _name, value ?? string.Empty);
        }

        /// <summary>The key's Windows virtual-key code; 0 for none, below 0 for VHS's mouse buttons.</summary>
        public int VirtualKey
        {
            get => _key;
            set => Change(ref _key, value);
        }

        /// <summary>The key as VHS wrote it in its list: "C-A-S-" for the modifiers, then the key; empty for none.</summary>
        public string KeyString
        {
            get
            {
                if (_key == 0)
                    return string.Empty;

                StringBuilder text = new StringBuilder();
                if (_control)
                    text.Append("C-");
                if (_alt)
                    text.Append("A-");
                if (_shift)
                    text.Append("S-");
                text.Append(KeyName(_key));
                return text.ToString();
            }
        }

        /// <summary>Raised when the hotkey's key is pressed, one handler at a time.</summary>
        public event EventHandler<cEatableFiredEventArgs> Fired2;

        [Obsolete("Use Fired2")]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public event EventHandler Fired;

        /// <summary>The key, modifiers and switch it was made with, again.</summary>
        public void ResetToDefaults()
        {
            _alt = _defaultAlt;
            _control = _defaultControl;
            _shift = _defaultShift;
            _enabled = _defaultEnabled;
            _key = _defaultKey;
            Changed();
        }

        public static int MAKE_JOYSTICK_VKEY(int joyid, int joybutton)
            => ((joyid << 16) & 0xFFF0000) | (joybutton & 0xFFFF) | 0x10000000;

        /// <summary>Raises it as though its key had been pressed. Only one that has been added can be.</summary>
        public void Fire()
        {
            if (!IsInitialized)
                throw new Exception("Cannot fire uninitialized hotkey.");

            Raise();
        }

        /// <summary>
        /// Raises the handlers, the old event's first; true when any of them ate the key. A handler
        /// that throws stops the rest, and its exception goes to whoever pressed the key - the host,
        /// which says whose handler it was.
        /// </summary>
        internal bool Raise()
        {
#pragma warning disable CS0618
            Fired?.Invoke(this, EventArgs.Empty);
#pragma warning restore CS0618

            bool eaten = false;
            foreach (Delegate handler in Fired2?.GetInvocationList() ?? Array.Empty<Delegate>())
            {
                cEatableFiredEventArgs args = new cEatableFiredEventArgs();
                ((EventHandler<cEatableFiredEventArgs>)handler)(this, args);
                eaten |= args.Eat;
            }

            return eaten || Fired2 == null;
        }

        /// <summary>The assembly that added it, so the host can let a stopped plugin's hotkeys go.</summary>
        internal Assembly Owner { get; set; }

        /// <summary>Whose and which, as the hotkey system files it: its owner's name and its own.</summary>
        internal string Key => AssemblyName + "&&&&" + _name;

        private void Change<T>(ref T field, T value)
        {
            field = value;
            Changed();
        }

        private void Changed() => VHotkeySystem.InstanceReal.OnChanged(this);

        /// <summary>A key as VHS named it in a key string.</summary>
        private static string KeyName(int key)
        {
            if ((key & 0xF0000000u) == 0x10000000)
                return "Joy" + ((key >> 16) & 0xFFF) + "B" + (key & 0xFFFF);

            return key switch
            {
                -1 => "mscrlup",
                -2 => "mscrldn",
                -3 => "mscrllft",
                -4 => "mscrlrt",
                -5 => "mdlclk",
                -6 => "mclick4",
                -7 => "mclick5",
                8 => "bkspc",
                9 => "tab",
                13 => "enter",
                19 => "pause",
                27 => "esc",
                32 => "space",
                33 => "pgup",
                34 => "pgdn",
                35 => "end",
                36 => "home",
                37 => "left",
                38 => "up",
                39 => "right",
                40 => "down",
                45 => "ins",
                46 => "del",
                >= 0x60 and <= 0x69 => "np" + (key - 0x60),
                >= 0x70 and <= 0x7F => "f" + (key - 0x6F),
                >= '0' and <= '9' or >= 'A' and <= 'Z' => ((char)key).ToString().ToLowerInvariant(),
                _ => "???",
            };
        }
    }
}
