using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Globalization;
using System.Security;
using AC.Host.Plugins.Views;
using Decal.Adapter.Hosting;
using VirindiViewService.Controls;

namespace VirindiViewService
{
    /// <summary>A view's title, size and icon, before it becomes a window.</summary>
    public class ViewProperties : IDisposable
    {
        public ViewProperties()
        {
        }

        public ViewProperties(ViewProperties p)
        {
            if (p == null)
                return;

            Title = p.Title;
            Icon = p.Icon;
            Width = p.Width;
            Height = p.Height;
            ShowInBar = p.ShowInBar;
        }

        public ViewProperties(string pTitle, int pWidth, int pHeight, ACImage pIcon)
            : this(pTitle, pWidth, pHeight, pIcon, true)
        {
        }

        public ViewProperties(string pTitle, int pWidth, int pHeight, ACImage pIcon, bool pShowInBar)
        {
            Title = pTitle;
            Width = pWidth;
            Height = pHeight;
            Icon = pIcon;
            ShowInBar = pShowInBar;
        }

        public virtual string Title { get; set; } = string.Empty;

        public virtual ACImage Icon { get; set; } = new ACImage();

        public virtual int Width { get; set; }

        public virtual int Height { get; set; }

        public virtual bool ShowInBar { get; set; } = true;

        public virtual void Dispose()
        {
        }
    }

    /// <summary>
    /// A view's controls: every one by name, which contains which, and the outermost.
    /// </summary>
    /// <remarks>
    /// Made by <see cref="XMLParsers.Decal3XMLParser"/> from the host's parse of the XML, with
    /// a VVS control attached to each host control, so looking a control up here gives the
    /// same object every time and the same state the overlay draws.
    ///
    /// <para>
    /// A group can also be built in code, as VVS allowed: controls made with <c>new</c> and put
    /// in a layout or a tab are given host controls of their own, named uniquely if the plugin
    /// did not name them, and join the group as parsed ones do. A group made empty, for a
    /// window a plugin builds entirely in code, has an empty layout as its outermost control,
    /// and the control the plugin makes its head is placed in it, filling the window.
    /// </para>
    /// </remarks>
    public class ControlGroup : IDisposable
    {
        private static uint _nextGroupId;

        private readonly Dictionary<string, HudControl> _byName = new Dictionary<string, HudControl>(StringComparer.Ordinal);
        private readonly Dictionary<ViewControl, HudControl> _byControl = new Dictionary<ViewControl, HudControl>();
        private readonly Dictionary<HudControl, HudControl> _parents = new Dictionary<HudControl, HudControl>();
        private HudControl _head;

        /// <summary>True for a group whose outermost layout was made to hold whatever the plugin makes its head.</summary>
        private bool _builtInCode;

        public ControlGroup()
        {
            GroupID = ++_nextGroupId;
        }

        internal ControlGroup(DecalView view)
            : this()
        {
            Load(view);
        }

        /// <summary>The host's view the controls belong to; null for a group made in code and not yet in a window.</summary>
        internal DecalView View { get; private set; }

        public uint GroupID { get; }

        public HudView ViewParentP { get; internal set; }

        public HudControl HeadControl
        {
            get => _head;
            set
            {
                if (ReferenceEquals(_head, value))
                    return;

                if (View == null)
                {
                    // Not in a window yet: remembered, and placed when the window is made.
                    _head = value;
                }
                else if (_builtInCode && value != null && !value.Attached)
                {
                    FixedLayout root = (FixedLayout)View.Root;
                    Place(value, root, _byControl[root], new Rectangle(0, 0, View.Width, View.Height));
                    _parents.Remove(value);
                    _head = value;
                }
                else
                {
                    DecalRuntime.Current?.NoteUnsupported("ControlGroup.HeadControl", "a parsed view's outermost control comes from its XML");
                }

                HeadControlChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public event EventHandler HeadControlChanged;

        /// <summary>The named control, or null for a name the view does not have.</summary>
        public HudControl this[string n] => n != null && _byName.TryGetValue(n, out HudControl control) ? control : null;

        public bool Contains(string n) => n != null && _byName.ContainsKey(n);

        public bool Contains(HudControl n) => n != null && _byControl.ContainsValue(n);

        public HudControl ParentOf(string ControlName) => ParentOf(this[ControlName]);

        public HudControl ParentOf(HudControl Control) => Control != null && _parents.TryGetValue(Control, out HudControl parent) ? parent : null;

        public bool HasChildren(HudControl Control) => ChildrenOf(Control).Count > 0;

        public ReadOnlyCollection<HudControl> ChildrenOf(HudControl Control)
        {
            List<HudControl> children = new List<HudControl>();
            foreach (KeyValuePair<HudControl, HudControl> pair in _parents)
            {
                if (ReferenceEquals(pair.Value, Control))
                    children.Add(pair.Key);
            }

            children.Sort();
            return children.AsReadOnly();
        }

        public string GetUniqueName() => GetUniqueName("Control");

        public string GetUniqueName(string Suggestion)
        {
            string stem = string.IsNullOrEmpty(Suggestion) ? "Control" : Suggestion;
            string name = stem;
            for (int n = 1; _byName.ContainsKey(name) || (View != null && View.Contains(name)); n++)
                name = stem + n.ToString(CultureInfo.InvariantCulture);

            return name;
        }

        internal HudControl Find(ViewControl control) => control != null && _byControl.TryGetValue(control, out HudControl hud) ? hud : null;

        /// <summary>
        /// Gives a control made in code a host control in a layout of this group's view, and
        /// joins it to the group - named as the plugin named it, or uniquely if it did not.
        /// </summary>
        internal void Place(HudControl control, FixedLayout layout, HudControl parent, Rectangle rect)
        {
            string wanted = control.InternalName;
            string name = wanted.Length > 0 && !Contains(wanted) && !View.Contains(wanted)
                ? wanted
                : GetUniqueName(wanted.Length > 0 ? wanted : control.GetType().Name);

            ViewControl made = control.CreateHostControl(View, layout, name, rect);
            Adopt(control, made, parent);
        }

        /// <summary>Joins a control made in code to a host control already in the view.</summary>
        internal void Adopt(HudControl control, ViewControl made, HudControl parent)
        {
            int number = 0;
            for (int i = 0; i < View.Controls.Count; i++)
            {
                if (ReferenceEquals(View.Controls[i], made))
                {
                    number = i + 1;
                    break;
                }
            }

            _byControl[made] = control;
            if (made.Name.Length > 0)
                _byName[made.Name] = control;
            if (parent != null)
                _parents[control] = parent;

            control.AttachMade(this, made, number);
        }

        /// <summary>
        /// Gives a group made in code, with no view yet, an empty one to live in - and places in
        /// it the head the plugin gave it, and so everything under that.
        /// </summary>
        internal void LoadEmpty(DecalView empty)
        {
            HudControl head = _head;
            _head = null;
            Load(empty);
            _builtInCode = true;

            if (head != null && !head.Attached)
                HeadControl = head;
        }

        internal static ControlGroup BuiltInCode(DecalView empty)
        {
            ControlGroup group = new ControlGroup();
            group.LoadEmpty(empty);
            return group;
        }

        public void Dispose()
        {
            foreach (HudControl control in _byControl.Values)
                control.Dispose();
        }

        private void Load(DecalView view)
        {
            View = view;
            view.Bar = ViewBar.Vvs;

            for (int i = 0; i < view.Controls.Count; i++)
            {
                ViewControl control = view.Controls[i];
                HudControl hud = Make(control);
                hud.Attach(this, control, i + 1);
                _byControl[control] = hud;

                if (control.Name.Length > 0 && !_byName.ContainsKey(control.Name))
                    _byName[control.Name] = hud;
            }

            foreach (ViewControl control in view.Controls)
            {
                IEnumerable<ViewControl> children = control switch
                {
                    FixedLayout layout => layout.Children,
                    Notebook notebook => PageContents(notebook),
                    _ => Array.Empty<ViewControl>(),
                };

                foreach (ViewControl child in children)
                    _parents[_byControl[child]] = _byControl[control];
            }

            _head = _byControl[view.Root];
        }

        private HudControl Make(ViewControl control)
            => control switch
            {
                PushButton => new HudButton(),
                ImageButton => new HudImageButton(),
                Checkbox => new HudCheckBox(),
                Edit => new HudTextBox(),
                Choice => new HudCombo(this),
                Slider => new HudHSlider(),
                List => new HudList(),
                StaticText => new HudStaticText(),
                Notebook => new HudTabView(),
                Progress => new HudProgressBar(),
                FixedLayout => new HudFixedLayout(),
                _ => new HudControl(),
            };

        private static IEnumerable<ViewControl> PageContents(Notebook notebook)
        {
            foreach (NotebookPage page in notebook.Pages)
                yield return page.Content;
        }
    }

    /// <summary>
    /// A VVS window: made from a parsed view, it opens as one of the host's overlay windows,
    /// labelled as the plugin whose code made it.
    /// </summary>
    public class HudView : ViewProperties, IDisposable
    {
        private static readonly List<HudView> _all = new List<HudView>();

        private readonly HostedView _hosted;
        private readonly ControlGroup _controls;
        private Point _location;
        private bool _disposed;

        public HudView()
            : this(string.Empty, 300, 200, new ACImage())
        {
        }

        public HudView(string pTitle, int pWidth, int pHeight, ACImage pIcon)
            : this(pTitle, pWidth, pHeight, pIcon, true, null)
        {
        }

        public HudView(string pTitle, int pWidth, int pHeight, ACImage pIcon, bool pShowInBar)
            : this(pTitle, pWidth, pHeight, pIcon, pShowInBar, null)
        {
        }

        public HudView(string pTitle, int pWidth, int pHeight, ACImage pIcon, bool pShowInBar, string pWindowKey)
            : this(pTitle, pWidth, pHeight, pIcon, pShowInBar, pWindowKey, true)
        {
        }

        /// <summary>
        /// A window with nothing in it yet. Controls put in it in code cannot be drawn - the
        /// host's views are made from XML - so this is an empty window with a title.
        /// </summary>
        public HudView(string pTitle, int pWidth, int pHeight, ACImage pIcon, bool pShowInBar, string pWindowKey, bool InitialDraw)
            : this(new ViewProperties(pTitle, pWidth, pHeight, pIcon, pShowInBar), ControlGroup.BuiltInCode(EmptyView(pTitle, pWidth, pHeight)), pWindowKey)
        {
        }

        public HudView(ViewProperties p, ControlGroup c)
            : this(p, c, (string)null)
        {
        }

        public HudView(ViewProperties p, ControlGroup c, HudViewDrawStyle s)
            : this(p, c, (string)null)
        {
        }

        public HudView(ViewProperties p, ControlGroup c, HudViewDrawStyle s, string pWindowKey)
            : this(p, c, pWindowKey)
        {
        }

        public HudView(ViewProperties p, ControlGroup c, string pWindowKey)
            : base(p)
        {
            _controls = c ?? throw new ArgumentNullException(nameof(c));

            DecalRuntime runtime = DecalRuntime.Current
                ?? throw new InvalidOperationException("Virindi View Service is not running: there is no Decal runtime to show the window in.");

            // A group built in code and not yet in a window gets an empty one now, and what the
            // plugin put in the group is placed in it.
            if (c.View == null)
                c.LoadEmpty(EmptyView(p?.Title, p?.Width > 0 ? p.Width : 300, p?.Height > 0 ? p.Height : 200));

            DecalView view = c.View;
            _hosted = runtime.AddView(view);
            WindowKey = pWindowKey ?? string.Empty;
            c.ViewParentP = this;

            // What the properties say wins over what the XML said, as it did in VVS: a plugin
            // that changed the title after parsing meant the change.
            if (p != null)
            {
                if (!string.IsNullOrEmpty(p.Title))
                    view.Title = p.Title;
                if (p.Width > 0)
                    view.Width = p.Width;
                if (p.Height > 0)
                    view.Height = p.Height;
                if (p.Icon != null && p.Icon.ToImageKey().Length > 0)
                    view.IconKey = p.Icon.ToImageKey();
            }

            // Windows the runtime has closed - their plugin stopped without disposing them - are
            // let go here, or they would keep a switched-off plugin from ever unloading.
            _all.RemoveAll(v => !v._hosted.IsOpen);
            _all.Add(this);
            ViewCreated?.Invoke(this, EventArgs.Empty);
        }

        public static event EventHandler ViewCreated;

        public static event EventHandler ViewDestroyed;

#pragma warning disable CS0067
        public static event EventHandler FocusChanged;

        public event EventHandler Resize;

        public event EventHandler Moved;

        public event EventHandler ThemeChanged;

        public event EventHandler TitleBarVisibleChanged;

        public event EventHandler GhostedChanged;

        public event EventHandler AlphaChanged;

        public event EventHandler ClickThroughChanged;

        public event EventHandler IconChanged;

        public event EventHandler ShowInBarChanged;
#pragma warning restore CS0067

        public event EventHandler VisibleChanged;

        /// <summary>The control with the keyboard. The overlay has the keyboard, so none.</summary>
        public static HudControl FocusControl { get; set; }

        public static ReadOnlyCollection<HudView> GetAllViews()
        {
            _all.RemoveAll(v => !v._hosted.IsOpen);
            return _all.AsReadOnly();
        }

        public static void SetPrimaryTheme(HudViewDrawStyle theme)
        {
        }

        /// <summary>The host's window for this view, for the host's side of things.</summary>
        internal HostedView Hosted => _hosted;

        public HudControl this[string cn] => _controls[cn];

        public ControlGroup Controls => _controls;

        public HudControl MainControl => _controls.HeadControl;

        public ulong ViewID => (ulong)_controls.GroupID;

        public string WindowKey { get; }

        public override string Title
        {
            get => _hosted?.View.Title ?? base.Title;
            set
            {
                base.Title = value;
                if (_hosted != null)
                    _hosted.View.Title = value;
            }
        }

        public override ACImage Icon
        {
            get => _hosted != null ? ACImage.FromImageKey(_hosted.View.IconKey) : base.Icon;
            set
            {
                base.Icon = value;
                if (_hosted != null)
                    _hosted.View.IconKey = value?.ToImageKey() ?? string.Empty;
            }
        }

        public override int Width
        {
            get => _hosted?.View.Width ?? base.Width;
            set
            {
                base.Width = value;
                if (_hosted != null)
                    _hosted.View.Width = value;
            }
        }

        public override int Height
        {
            get => _hosted?.View.Height ?? base.Height;
            set
            {
                base.Height = value;
                if (_hosted != null)
                    _hosted.View.Height = value;
            }
        }

        public override bool ShowInBar { get; set; } = true;

        /// <summary>Whether the plugin wants the window open.</summary>
        public bool Visible
        {
            get => _hosted.Visible;
            set
            {
                if (_hosted.Visible == value)
                    return;

                _hosted.Visible = value;
                VisibleChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>Where the window is. The overlay places windows itself; this is remembered, not obeyed.</summary>
        public Point Location
        {
            get => _location;
            set => _location = value;
        }

        public Size ClientArea
        {
            get => new Size(Width, Height);
            set
            {
                Width = value.Width;
                Height = value.Height;
            }
        }

        public Size TotalSize => ClientArea;

        public Rectangle HeadClipRegion => new Rectangle(Point.Empty, ClientArea);

        public bool IsTopView => false;

        public int Alpha { get; set; } = 255;

        public HudViewDrawStyle Theme { get; set; } = HudViewDrawStyle.Theme_Default;

        public bool UserResizeable { get; set; }

        public Size MinimumClientArea { get; set; }

        public Size MaximumClientArea { get; set; }

        public bool UserMinimizable { get; set; }

        public bool UserAlphaChangeable { get; set; }

        public bool ShowIcon { get; set; } = true;

        public bool UserClickThroughable { get; set; }

        public bool ClickThrough { get; set; }

        public bool TitlebarVisible => true;

        public bool UserGhostable { get; set; }

        public bool SpookyTabs { get; set; }

        public bool Ghosted { get; set; }

        public bool GhostStickyLeft { get; set; }

        public bool GhostStickyTop { get; set; }

        public bool GhostStickyRight { get; set; }

        public bool GhostStickyBottom { get; set; }

        public int ForcedZOrder { get; set; }

        public bool DirectRenderDraw { get; set; }

        public void LoadUserSettings()
        {
        }

        /// <summary>A button in the window's title bar. The overlay draws its own title bar, so it has none.</summary>
        public cUserWindowButton CreateWindowButton(ACImage upimg, ACImage downimg, int zorder)
        {
            DecalRuntime.Current?.NoteUnsupported("HudView.CreateWindowButton");
            return new cUserWindowButton { UpImage = upimg, DownImage = downimg, ZOrder = zorder };
        }

        public void RemoveWindowButton(cUserWindowButton btn)
        {
        }

        public void ClearWindowButtons()
        {
        }

        public ReadOnlyCollection<cUserWindowButton> GetWindowButtons() => new ReadOnlyCollection<cUserWindowButton>(Array.Empty<cUserWindowButton>());

        public override void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _all.Remove(this);
            DecalRuntime.Current?.RemoveView(_hosted);
            ViewDestroyed?.Invoke(this, EventArgs.Empty);
            base.Dispose();
        }

        /// <summary>The XML for a window with a title and nothing in it.</summary>
        private static DecalView EmptyView(string title, int width, int height)
            => DecalView.Parse(string.Create(CultureInfo.InvariantCulture,
                $"<view title=\"{SecurityElement.Escape(title ?? string.Empty)}\" width=\"{width}\" height=\"{height}\"><control progid=\"DecalControls.FixedLayout\"/></view>"));

        public class cUserWindowButton : IDisposable
        {
            internal cUserWindowButton()
            {
            }

#pragma warning disable CS0067
            public event EventHandler Hit;
#pragma warning restore CS0067

            public ACImage UpImage { get; set; }

            public ACImage DownImage { get; set; }

            public int ZOrder { get; set; }

            public string FriendlyName { get; set; } = string.Empty;

            public bool ShowInBar { get; set; }

            public bool ShowInMenu { get; set; }

            public void Dispose()
            {
            }
        }
    }

    /// <summary>Tooltips on controls. The overlay does not draw them; they are remembered for plugins that read them back.</summary>
    public static class TooltipSystem
    {
        public static cTooltipInfo AssociateTooltip(HudControl Target, string Text) => new cTooltipInfo(Target, Text);

        public static void RemoveTooltip(cTooltipInfo Info)
        {
        }

        public static void ShowTip(cTooltipInfo tip, Point pt)
        {
        }

        public class cTooltipInfo : IDisposable
        {
            internal cTooltipInfo(HudControl control, string text)
            {
                Control = control;
                Text = text ?? string.Empty;
            }

            public HudControl Control { get; }

            public string Text { get; }

            public void Dispose()
            {
            }
        }
    }
}
