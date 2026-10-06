using System;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace AC.Host.Actions
{
    /// <summary>
    /// The client's plugin bar - the dock of AC:Unreal's own plugins, one button per plugin - as its
    /// settings say it was left, or where it starts: in the client's interface units, which its
    /// Desktop UI Scale multiplies into pixels.
    /// </summary>
    /// <remarks>
    /// From the client's code (docs/ac-unreal-96-notes.md, section 8.2): the bar is a movable frame
    /// kept as <c>"__bar"</c> in settings.json's <c>_window_positions</c>, made 8 in from the
    /// window's left and 80 down, 62 wide and 120 high. It grows with its buttons: the live test's
    /// screenshot of 2026-10-06 has it in the world at 8,80 to 70,250 with four - the plugin list's
    /// "+", LOOT, NAV and the map - and none at the character select. So it is taken as 214 high,
    /// room for a fifth button, UCM's, once that is enabled.
    /// </remarks>
    public sealed record ClientPluginBar(double X, double Y, double Width, double Height, bool Saved)
    {
        /// <summary>Where the client starts it before the player moves it, as tall as five buttons make it.</summary>
        public static readonly ClientPluginBar Default = new ClientPluginBar(8, 80, 62, 214, false);

        /// <summary>The key the client keeps it under in settings.json's <c>_window_positions</c>.</summary>
        public const string PositionKey = "__bar";

        /// <summary>
        /// The bar as the client's <c>Saved\ClientPlugins\settings.json</c> keeps it:
        /// <c>_window_positions.__bar</c> for where the player left it, and <c>__bar.size</c> for a
        /// size, as the client keeps its windows'; <see cref="Default"/> for each part it does not have.
        /// </summary>
        public static ClientPluginBar Read(string settingsJson)
        {
            if (string.IsNullOrWhiteSpace(settingsJson))
                return Default;

            try
            {
                using JsonDocument document = JsonDocument.Parse(settingsJson, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty("_window_positions", out JsonElement positions)
                    || positions.ValueKind != JsonValueKind.Object)
                {
                    return Default;
                }

                bool moved = TryReadPoint(positions, PositionKey, out double x, out double y);
                bool sized = TryReadPoint(positions, PositionKey + ".size", out double width, out double height) && width > 0 && height > 0;
                return new ClientPluginBar(
                    moved ? x : Default.X,
                    moved ? y : Default.Y,
                    sized ? width : Default.Width,
                    sized ? height : Default.Height,
                    moved || sized);
            }
            catch (JsonException)
            {
                return Default;
            }
        }

        private static bool TryReadPoint(JsonElement positions, string key, out double x, out double y)
        {
            x = y = 0;
            return positions.TryGetProperty(key, out JsonElement point)
                   && point.ValueKind == JsonValueKind.Object
                   && point.TryGetProperty("x", out JsonElement px) && px.ValueKind == JsonValueKind.Number && px.TryGetDouble(out x)
                   && point.TryGetProperty("y", out JsonElement py) && py.ValueKind == JsonValueKind.Number && py.TryGetDouble(out y)
                   && double.IsFinite(x) && double.IsFinite(y);
        }

        /// <summary>"8,80 62x214 (where the client starts it)", in interface units.</summary>
        public string Describe()
            => FormattableString.Invariant($"{X:0.##},{Y:0.##} {Width:0.##}x{Height:0.##}") + (Saved ? " (where the player left it)" : " (where the client starts it)");
    }

    /// <summary>
    /// What the client's own display settings change of where it draws things: its Desktop UI Scale.
    /// </summary>
    /// <remarks>
    /// <para>
    /// AC:Unreal keeps the option in <c>Saved\Config\Windows\GameUserSettings.ini</c>, under
    /// <c>[ACE.Presentation]</c>, as <c>DesktopUIScale=1</c> - 1 to 3, in quarter steps, 1 when
    /// the line is missing. It is applied as Unreal's own DPI scale for the whole game viewport, so
    /// every one of the client's widgets is drawn that much larger, the character select among
    /// them; the window, its swap chain and the overlay's own drawing are not changed by it.
    /// </para>
    /// <para>
    /// The client limits the scale to what fits the window: the largest quarter step at which its
    /// 800 by 600 layout fits, never below 1 - so 200% in a 1920 by 1080 window is drawn at 175%.
    /// The overlay, which knows the window, applies that limit; this reads what the player chose.
    /// </para>
    /// </remarks>
    public static class ClientDisplay
    {
        /// <summary>The client's own bounds for the option, and its value when the line is missing.</summary>
        public const double MinUiScale = 1.0, MaxUiScale = 3.0, DefaultUiScale = 1.0;

        /// <summary>The ini section and key the client keeps the option under.</summary>
        public const string Section = "ACE.Presentation", Key = "DesktopUIScale";

        /// <summary>
        /// The Desktop UI Scale the player chose, from the text of the client's GameUserSettings.ini,
        /// as the client reads it: clamped to 1 to 3 and taken to the nearest quarter step; 1 when
        /// the line is missing or is not a number.
        /// </summary>
        public static double ReadDesktopUiScale(string ini)
        {
            if (string.IsNullOrEmpty(ini))
                return DefaultUiScale;

            bool inSection = false;
            using StringReader reader = new StringReader(ini);
            for (string line = reader.ReadLine(); line != null; line = reader.ReadLine())
            {
                string text = line.Trim();
                if (text.Length == 0 || text[0] == ';' || text[0] == '#')
                    continue;

                if (text[0] == '[')
                {
                    inSection = string.Equals(text.Trim('[', ']').Trim(), Section, StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                int equals = text.IndexOf('=');
                if (!inSection || equals <= 0 || !string.Equals(text.Substring(0, equals).Trim(), Key, StringComparison.OrdinalIgnoreCase))
                    continue;

                return double.TryParse(text.Substring(equals + 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                    ? QuarterStep(value)
                    : DefaultUiScale;
            }

            return DefaultUiScale;
        }

        /// <summary>A scale as the client takes it: within 1 to 3, at the nearest quarter step. 1 for one that is not a number.</summary>
        public static double QuarterStep(double scale)
        {
            if (!double.IsFinite(scale))
                return DefaultUiScale;

            return Math.Floor(Math.Clamp(scale, MinUiScale, MaxUiScale) * 4.0 + 0.5) / 4.0;
        }

        /// <summary>"175%".</summary>
        public static string Percent(double scale) => FormattableString.Invariant($"{scale * 100:0.##}%");
    }
}
