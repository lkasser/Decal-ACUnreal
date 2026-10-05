using System.Collections.Generic;
using AC.Host.Plugins;
using AC.Host.World;

namespace AC.Host.Runtime
{
    /// <summary>
    /// Turns the host's own state into the snapshot the overlay draws.
    /// </summary>
    internal static class OverlaySnapshot
    {
        /// <summary>
        /// Builds a snapshot of everything the plugins are showing and offers it to the overlay.
        /// Game thread only: that is the one place a plugin's panels can be read safely.
        /// </summary>
        /// <remarks>
        /// The only place the two vocabularies meet. Plugins publish generic panels and
        /// the overlay draws generic panels, so this converts and nothing more - which is
        /// what keeps the injected code free of any knowledge of loot, spells or the wire.
        /// </remarks>
        public static void Publish(GameHost host, AC.Host.Overlay.OverlayServer server, AC.Host.Overlay.OverlayImagePublisher images,
                                   AC.Host.Overlay.ViewLooks looks = null, AC.Host.Overlay.OverlayDecalBar decalBar = null)
        {
            ICharacterView character = host.Character;
            HostStatistics s = host.Statistics;

            List<(string, IReadOnlyList<string>, IReadOnlyList<AC.Host.Overlay.OverlayRow>)> panels =
                new List<(string, IReadOnlyList<string>, IReadOnlyList<AC.Host.Overlay.OverlayRow>)>();

            IReadOnlyList<OverlayPanel> collected = host.CollectPanels();

            foreach (OverlayPanel panel in collected)
            {
                List<AC.Host.Overlay.OverlayRow> rows = new List<AC.Host.Overlay.OverlayRow>(panel.Rows.Count);

                foreach (OverlayRow row in panel.Rows)
                {
                    AC.Host.Overlay.OverlayRow converted = AC.Host.Overlay.OverlayStateBuilder.Row(
                        (int)row.Tone,
                        row.Cells is string[] cells ? cells : System.Linq.Enumerable.ToArray(row.Cells));

                    converted.Id = row.Id;
                    rows.Add(converted);
                }

                panels.Add((panel.Title, panel.Columns, rows));
            }

            AC.Host.Overlay.OverlayState state = AC.Host.Overlay.OverlayStateBuilder.Build(
                connected: !string.IsNullOrEmpty(host.World.ServerName),
                acting: host.Actions.IsAvailable,
                looting: false,
                server: host.World.ServerName ?? string.Empty,
                character: character.Id == 0 ? "not logged in" : $"{character.Name} ({character.Level})",
                position: character.Location.HasValue ? character.Location.Value.ToString() : string.Empty,
                messagesIn: s.MessagesInbound,
                messagesOut: s.MessagesOutbound,
                malformed: s.Malformed,
                objects: host.World.ObjectCount,
                panels: panels.ToArray());

            // The builder takes titles, columns and rows; ownership and identity travel
            // separately because they are the host's to assert, not the panel's. Both
            // lists are in the same order, so this pairs them up.
            for (int i = 0; i < collected.Count && i < state.Panels.Count; i++)
            {
                state.Panels[i].Owner = collected[i].Owner;
                state.Panels[i].Key = collected[i].Key;
            }

            // Every loaded plugin's window, with its controls and its Decal view, whether or
            // not it has panels this time. The overlay lists these on its bar, so a quiet
            // plugin is still there to be opened.
            foreach (OverlayWindowInfo window in host.CollectWindows())
            {
                AC.Host.Overlay.OverlayWindow dto = AC.Host.Overlay.OverlayMapping.ToDto(window);
                looks?.Apply(dto.View, window.View);
                state.Windows.Add(dto);
            }

            // How windows are drawn and the bar laid out, as the player had them in the standard
            // client; and whoever is waiting for the next key the player presses.
            state.DefaultTheme = looks?.DefaultTheme ?? string.Empty;
            state.DecalBar = decalBar;
            state.VvsBar = looks?.VvsBar;
            state.KeyCapture = host.KeyCaptureOwner ?? string.Empty;

            foreach (BoundHotkey hotkey in host.CollectHotkeys())
            {
                // A hotkey switched off keeps its key, but the key goes to the game.
                if (hotkey.Keys.IsEmpty || host.DisabledHotkeys.Contains(hotkey.Name))
                    continue;

                state.Hotkeys.Add(new AC.Host.Overlay.OverlayHotkey
                {
                    Owner = hotkey.Owner,
                    Id = hotkey.Definition.Id,
                    Key = hotkey.Keys.Key,
                    Ctrl = hotkey.Keys.Ctrl,
                    Shift = hotkey.Keys.Shift,
                    Alt = hotkey.Keys.Alt,
                });
            }

            // Any image a view names that the overlay has not been sent - an icon, a
            // button's face - goes before the snapshot that names it, and so does anything the
            // overlay asked for.
            images?.PublishWanted();
            images?.PublishImagesNamedIn(state);

            server.Publish(state);
        }
    }
}
