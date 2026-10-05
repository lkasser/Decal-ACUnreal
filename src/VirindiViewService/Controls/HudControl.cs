using System;
using System.Drawing;
using AC.Host.Plugins.Views;

namespace VirindiViewService.Controls
{
    /// <summary>
    /// What every VVS control is: a named thing in a view, visible or not, raising mouse and
    /// focus events.
    /// </summary>
    /// <remarks>
    /// A control parsed from a view's XML is attached to the host's control of the same name,
    /// and reads and writes that control's state, so what a plugin sets is what the overlay
    /// draws. A control made with <c>new</c> and never put in a view keeps its state to itself:
    /// it can be set and read, but there is nothing to draw it on.
    ///
    /// <para>
    /// The player's actions arrive as the host control's events and are raised here as VVS
    /// raised them - a press is a mouse-down then a hit, a finished edit is a change then the
    /// loss of focus - because plugins, and the view wrappers compiled into them, listen for
    /// exactly those.
    /// </para>
    /// </remarks>
    public class HudControl : IComparable, IDisposable
    {
        private bool _visible = true;
        private string _name = string.Empty;

        public HudControl()
        {
        }

        /// <summary>The host control this one is, or null for one made in code and not in a view.</summary>
        internal ViewControl Bound { get; private set; }

        internal bool Attached => Bound != null;

        public string Name => Bound?.Name ?? _name;

        /// <summary>A name VVS used internally; kept for plugins that set it.</summary>
        public string InternalName
        {
            get => _name;
            set => _name = value ?? string.Empty;
        }

        /// <summary>The control's number in its view, which is what VVS's events carried.</summary>
        public int XMLID { get; private set; }

        public ControlGroup Group { get; private set; }

        public virtual bool Visible
        {
            get => Bound?.Visible ?? _visible;
            set
            {
                if (Bound != null)
                    Bound.Visible = value;
                else
                    _visible = value;
            }
        }

        public virtual bool CanDraw { get; set; } = true;

        public bool HasFocus { get; internal set; }

        public bool MouseOver => false;

        public bool OnScreenNow => Visible;

        public bool ViewVisible => Group?.ViewParentP?.Visible ?? false;

        /// <summary>Where the control is within its layout, in the view's pixels.</summary>
        public Rectangle ClipRegion => Bound != null ? new Rectangle(Bound.Left, Bound.Top, Bound.Width, Bound.Height) : Rectangle.Empty;

        public Rectangle SavedClipRegion => ClipRegion;

        public Rectangle SavedViewRect => ClipRegion;

        public ControlGroup SavedGroup => Group;

        public HudViewDrawStyle Theme => HudViewDrawStyle.Theme_Default;

        public HudViewDrawStyle SavedStyle => HudViewDrawStyle.Theme_Default;

        protected bool Initialized => Attached;

        public event EventHandler Disposing;

        public event EventHandler GotFocus;

        public event EventHandler LostFocus;

        public event EventHandler<ControlMouseEventArgs> MouseEvent;

        public event EventHandler Hit;

#pragma warning disable CS0067
        public event EventHandler<ControlKeyEventArgs> KeyEvent;

        public event EventHandler DrawStateChange;

        public event EventHandler MouseOverChange;

        public event EventHandler ThemeChanged;

        public event EventHandler DrawBegun;
#pragma warning restore CS0067

        /// <summary>Joins the control to its view: the group it is in, the host control it is, and its number.</summary>
        internal void Attach(ControlGroup group, ViewControl control, int xmlId)
        {
            Group = group;
            Bound = control;
            XMLID = xmlId;
            OnAttached();
        }

        /// <summary>
        /// Joins a control made in code to the host control just made for it, then hands over
        /// what the plugin set on it while it had nowhere to be drawn.
        /// </summary>
        internal void AttachMade(ControlGroup group, ViewControl control, int xmlId)
        {
            Attach(group, control, xmlId);
            ApplyDetachedState();
        }

        /// <summary>
        /// Makes the host control this kind of control is drawn as, in a layout of a view. A
        /// plain control, with nothing to show, is drawn as an empty label.
        /// </summary>
        internal virtual ViewControl CreateHostControl(DecalView view, FixedLayout parent, string name, Rectangle rect)
            => view.AddControl<StaticText>(parent, name, rect.X, rect.Y, rect.Width, rect.Height);

        /// <summary>Where a control kind subscribes to its host control's events.</summary>
        private protected virtual void OnAttached()
        {
        }

        /// <summary>
        /// Hands state set in code before the control was in a view to the host control. Never
        /// called for a control parsed from XML, whose state the XML set.
        /// </summary>
        private protected virtual void ApplyDetachedState()
        {
            if (!_visible)
                Bound.Visible = false;
        }

        private protected virtual void OnDetaching()
        {
        }

        /// <summary>Raises a press as VVS reported one: the button going down, coming up, and the hit.</summary>
        private protected void RaisePress()
        {
            RaiseMouse(ControlMouseEventArgs.MouseEventType.MouseDown);
            RaiseMouse(ControlMouseEventArgs.MouseEventType.MouseUp);
            RaiseMouse(ControlMouseEventArgs.MouseEventType.MouseHit);
            Hit?.Invoke(this, EventArgs.Empty);
        }

        private protected void RaiseMouse(ControlMouseEventArgs.MouseEventType type)
            => MouseEvent?.Invoke(this, new ControlMouseEventArgs(type, ClipRegion.X, ClipRegion.Y));

        /// <summary>
        /// The player finished with the control. VVS's view wrappers only count the loss of focus
        /// as the end of an edit while the control still says it has focus, so it does, until
        /// the event is over.
        /// </summary>
        private protected void RaiseFocusCycle()
        {
            HasFocus = true;
            GotFocus?.Invoke(this, EventArgs.Empty);
            try
            {
                LostFocus?.Invoke(this, EventArgs.Empty);
            }
            finally
            {
                HasFocus = false;
            }
        }

        public int CompareTo(object obj) => obj is HudControl other ? XMLID.CompareTo(other.XMLID) : 1;

        public virtual void Invalidate()
        {
        }

        public virtual void InvalidateParent()
        {
        }

        /// <summary>
        /// Where VVS had a control draw itself on its window's surface, each frame. The overlay
        /// draws the windows here, so the host never calls it; a control that draws itself
        /// (<see cref="HudEmulator"/>) does its drawing when a plugin does.
        /// </summary>
        public virtual void DrawNow(DxTexture iSavedTarget)
        {
        }

        public virtual void MouseWheel(Point pt, int amt)
        {
        }

        public virtual void MouseDown(Point pt)
        {
        }

        public virtual void MouseUp(Point pt, Point orig)
        {
        }

        public virtual void ExternalMouseUp(Point pt)
        {
        }

        public virtual void MouseMove(Point pt)
        {
        }

        public virtual void KeyDown(int vkey, byte scancode, bool extended, ushort repeatcount, ref bool Eat)
        {
        }

        public virtual void KeyUp(int vkey, byte scancode, bool extended, ushort repeatcount, ref bool Eat)
        {
        }

        public virtual void KeyChar(byte scancode, bool extended, char charcode, ref bool Eat)
        {
        }

        public virtual void RawKeyAction(short Msg, int WParam, int LParam, ref bool Eat)
        {
        }

        public virtual void RemovedChild(HudControl ch)
        {
        }

        protected virtual void AddChild(HudControl ctrl)
        {
        }

        protected internal virtual void Initialize()
        {
        }

        public virtual void Dispose()
        {
            Disposing?.Invoke(this, EventArgs.Empty);
            if (Bound != null)
                OnDetaching();
        }

        /// <summary>Thrown by VVS for a control used before it was in a view. Kept so plugins that catch it compile.</summary>
        public class ControlNotInitializedException : Exception
        {
            public ControlNotInitializedException()
            {
            }

            public ControlNotInitializedException(string message)
                : base(message)
            {
            }
        }
    }

    public class ControlMouseEventArgs : EventArgs
    {
        public enum MouseEventType
        {
            MouseDown = 0,
            MouseUp = 1,
            MouseMove = 2,
            MouseWheel = 3,
            MouseHit = 4,
        }

        public enum MouseButton
        {
            Left = 0,
            Right = 1,
            None = 2,
        }

        internal ControlMouseEventArgs(MouseEventType type, int x, int y)
        {
            EventType = type;
            X = x;
            Y = y;
            InitialX = x;
            InitialY = y;
            Button = MouseButton.Left;
        }

        public MouseEventType EventType { get; }

        public int X { get; }

        public int Y { get; }

        public MouseButton Button { get; }

        public int WheelAmount => 0;

        public int InitialX { get; }

        public int InitialY { get; }
    }

    public class ControlKeyEventArgs : EventArgs
    {
        public enum KeyEventType
        {
            KeyDown = 0,
            KeyUp = 1,
            KeyPress = 2,
        }

        internal ControlKeyEventArgs(KeyEventType type)
        {
            EventType = type;
        }

        /// <summary>Never raised: the overlay has the keyboard and sends controls finished values, not keys.</summary>
        public KeyEventType EventType { get; }

        public bool Eat { get; set; }

        public char Char { get; }

        public byte ScanCode { get; }

        public bool IsExtendedKey { get; }

        public ushort RepeatCount { get; }
    }

    public enum eTextBoxInputType
    {
        Any = 1,
        Int_Numeric = 2,
        Float_Numeric = 3,
    }
}
