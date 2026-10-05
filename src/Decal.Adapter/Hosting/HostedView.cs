using System;
using System.Collections.Generic;
using AC.Host.Plugins.Views;

namespace Decal.Adapter.Hosting
{
    /// <summary>
    /// A window a Decal plugin has put up, through Decal's own views or Virindi View Service's:
    /// its parsed view, whose it is, and the numbers its controls answer to.
    /// </summary>
    /// <remarks>
    /// Both view systems end up here, as one <see cref="DecalView"/> each, which is what the
    /// host's overlay already knows how to draw and how to send clicks back to. The wrapper a
    /// plugin holds - a Decal ViewWrapper or a VVS HudView - is a face on one of these.
    /// </remarks>
    public sealed class HostedView
    {
        private readonly Dictionary<ViewControl, int> _ids = new Dictionary<ViewControl, int>();

        internal HostedView(string owner, string key, DecalView view)
        {
            Owner = owner ?? string.Empty;
            Key = key ?? string.Empty;
            View = view ?? throw new ArgumentNullException(nameof(view));

            Renumber();
        }

        /// <summary>The friendly name of the plugin whose window it is.</summary>
        public string Owner { get; }

        /// <summary>
        /// Unique among the runtime's windows and stable from one session to the next, so the
        /// overlay can remember where the player left it: the owner's name, and a number after
        /// it for a plugin's second and later windows.
        /// </summary>
        public string Key { get; }

        public DecalView View { get; }

        /// <summary>
        /// Whether the plugin wants the window open - Decal's Activated, VVS's Visible. False
        /// until the plugin says otherwise, as in Decal, where a plugin's window waited on the
        /// bar until the player clicked it. The overlay takes it as where the window starts;
        /// after that, opening and closing it is the player's business.
        /// </summary>
        public bool Visible { get; set; }

        /// <summary>False once the plugin has disposed of it; the runtime drops it then.</summary>
        public bool IsOpen { get; internal set; } = true;

        /// <summary>The number a control answers to in events, or 0 for one not in this view.</summary>
        public int IdOf(ViewControl control)
        {
            if (control == null)
                return 0;

            // A control added since - a plugin building its window in code - is numbered on
            // first asking; the ones before it keep their numbers.
            if (!_ids.ContainsKey(control) && _ids.Count < View.Controls.Count)
                Renumber();

            return _ids.TryGetValue(control, out int id) ? id : 0;
        }

        /// <summary>
        /// Decal numbered a view's controls as it made them, and plugins compare an event's Id
        /// with a control's; numbering in order from 1 keeps both sides agreeing and leaves 0
        /// meaning "no control". Controls are only ever added at the end, so numbers never move.
        /// </summary>
        private void Renumber()
        {
            for (int i = 0; i < View.Controls.Count; i++)
                _ids[View.Controls[i]] = i + 1;
        }

        public override string ToString() => $"{Key} ({View.Title})";
    }
}
