using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace AC.Host.Plugins
{
    /// <summary>
    /// One of AC:Unreal's own client plugins that the player has enabled in the client's plugin
    /// list, with the permissions granted to it there.
    /// </summary>
    public sealed class ClientPluginGrant : IEquatable<ClientPluginGrant>
    {
        /// <summary>
        /// The permissions that let a client plugin play the character - cast, fight, walk, use
        /// and move items, loot - and so run into Virindi Tank's macro. "say", "fellowship" and
        /// "confirm" are left out: they cannot fight over a target, a buff or a corpse.
        /// </summary>
        public static readonly IReadOnlyList<string> AutomationPermissions = new[] { "cast", "combat", "navigation", "inventory", "loot" };

        public ClientPluginGrant(string id, IEnumerable<string> permissions)
        {
            Id = id ?? string.Empty;
            Permissions = (permissions ?? Enumerable.Empty<string>()).ToArray();
        }

        /// <summary>The plugin's id, as its plugin.json names it: "ucm" for the Unattended Combat Manager.</summary>
        public string Id { get; }

        /// <summary>What to call it in a sentence: "UCM", as the client's own windows do, or else its id.</summary>
        public string Name => string.Equals(Id, ClientPluginsState.UcmId, StringComparison.OrdinalIgnoreCase) ? "UCM" : Id;

        /// <summary>Every permission granted to it, in the order the client wrote them.</summary>
        public IReadOnlyList<string> Permissions { get; }

        /// <summary>The granted permissions that are <see cref="AutomationPermissions"/>.</summary>
        public IReadOnlyList<string> AutomationGranted
            => Permissions.Where(p => AutomationPermissions.Contains(p, StringComparer.OrdinalIgnoreCase)).ToArray();

        /// <summary>Whether it holds any of the <see cref="AutomationPermissions"/>.</summary>
        public bool Automates => AutomationGranted.Count > 0;

        /// <summary>"UCM (cast, combat, navigation)", or "waypoint (no permissions)".</summary>
        public string Describe() => $"{Name} ({(Permissions.Count == 0 ? "no permissions" : string.Join(", ", Permissions))})";

        public bool Equals(ClientPluginGrant other)
            => other is not null && string.Equals(Id, other.Id, StringComparison.Ordinal) && Permissions.SequenceEqual(other.Permissions, StringComparer.Ordinal);

        public override bool Equals(object obj) => Equals(obj as ClientPluginGrant);

        public override int GetHashCode() => HashCode.Combine(Id, Permissions.Count);

        public override string ToString() => Describe();
    }

    /// <summary>
    /// Which of AC:Unreal's own client plugins the player has enabled, as the client keeps it in
    /// <c>Saved\ClientPlugins\settings.json</c>: UCM, its Unattended Combat Manager - a Virindi
    /// Tank of the client's own - among them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Since release 94 the client runs sandboxed Lua plugins. Each is off until the player enables
    /// it in the client's plugin list (<c>/plugins</c>) and grants it permissions; the client then
    /// keeps <c>"&lt;id&gt;": "enabled:cast,combat,..."</c> in that file, and
    /// <c>"&lt;id&gt;": "disabled"</c> once switched off. See docs/ac-unreal-96-notes.md.
    /// </para>
    /// <para>
    /// Enabled is all that can be seen from outside. Enabling never starts a plugin: UCM acts only
    /// once its Start is pressed, each session, and stops on manual movement or leaving the world -
    /// and whether it is running the client writes nowhere. So an enabled UCM is a plugin that may
    /// be running, not one that is.
    /// </para>
    /// </remarks>
    public sealed class ClientPluginsState : IEquatable<ClientPluginsState>
    {
        /// <summary>The id of the Unattended Combat Manager.</summary>
        public const string UcmId = "ucm";

        /// <summary>What the client's settings.json starts a grant with.</summary>
        public const string EnabledPrefix = "enabled:";

        /// <summary>What it keeps for a plugin switched off.</summary>
        public const string DisabledValue = "disabled";

        /// <summary>Nothing read: no client found, or its settings could not be read.</summary>
        public static readonly ClientPluginsState Unknown = new ClientPluginsState(false, Array.Empty<ClientPluginGrant>(), Array.Empty<string>());

        /// <summary>The client's settings read, and no plugin enabled or switched off in them - as before the plugin list was first opened.</summary>
        public static readonly ClientPluginsState NoneEnabled = new ClientPluginsState(true, Array.Empty<ClientPluginGrant>(), Array.Empty<string>());

        public ClientPluginsState(bool known, IEnumerable<ClientPluginGrant> enabled, IEnumerable<string> disabled)
        {
            Known = known;
            Enabled = (enabled ?? Enumerable.Empty<ClientPluginGrant>()).ToArray();
            Disabled = (disabled ?? Enumerable.Empty<string>()).ToArray();
        }

        /// <summary>Whether the client's settings were read; false while there is no client to read them from.</summary>
        public bool Known { get; }

        /// <summary>Every client plugin the player has enabled, with what it was granted.</summary>
        public IReadOnlyList<ClientPluginGrant> Enabled { get; }

        /// <summary>The ids of the client plugins the player has switched off.</summary>
        public IReadOnlyList<string> Disabled { get; }

        /// <summary>Whether the Unattended Combat Manager is enabled - which is not to say running.</summary>
        public bool UcmEnabled => Enabled.Any(p => string.Equals(p.Id, UcmId, StringComparison.OrdinalIgnoreCase));

        /// <summary>The enabled plugins that hold an automation permission: UCM, or another that could play the character.</summary>
        public IReadOnlyList<ClientPluginGrant> Automating => Enabled.Where(p => p.Automates).ToArray();

        /// <summary>Whether any enabled client plugin holds an automation permission.</summary>
        public bool AutomationEnabled => Enabled.Any(p => p.Automates);

        /// <summary>
        /// A few words for a log line or a status line: "enabled: UCM (cast, combat, ...)", "none
        /// enabled", "not known (the client's settings were not read)".
        /// </summary>
        public string Describe()
        {
            if (!Known)
                return "not known (the client's settings were not read)";

            if (Enabled.Count == 0)
                return Disabled.Count == 0 ? "none enabled" : $"none enabled ({string.Join(", ", Disabled)} switched off)";

            return "enabled: " + string.Join("; ", Enabled.Select(p => p.Describe()));
        }

        /// <summary>
        /// Reads the client's <c>Saved\ClientPlugins\settings.json</c>: every top-level string that
        /// begins "enabled:" is a plugin's grant, its permissions after the colon, and every
        /// "disabled" one switched off. Everything else in it - window positions, profiles, folders -
        /// is passed over. <see cref="Unknown"/> for text that is not a JSON object.
        /// </summary>
        public static ClientPluginsState Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return NoneEnabled;

            try
            {
                using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    return Unknown;

                List<ClientPluginGrant> enabled = new List<ClientPluginGrant>();
                List<string> disabled = new List<string>();
                foreach (JsonProperty property in document.RootElement.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.String || property.Name.Length == 0 || property.Name.StartsWith('_'))
                        continue;

                    string value = property.Value.GetString() ?? string.Empty;
                    if (value.StartsWith(EnabledPrefix, StringComparison.Ordinal))
                    {
                        string[] permissions = value.Substring(EnabledPrefix.Length)
                            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        enabled.Add(new ClientPluginGrant(property.Name, permissions));
                    }
                    else if (string.Equals(value, DisabledValue, StringComparison.Ordinal))
                    {
                        disabled.Add(property.Name);
                    }
                }

                return new ClientPluginsState(true, enabled, disabled);
            }
            catch (JsonException)
            {
                return Unknown;
            }
        }

        public bool Equals(ClientPluginsState other)
            => other is not null && Known == other.Known && Enabled.SequenceEqual(other.Enabled) && Disabled.SequenceEqual(other.Disabled, StringComparer.Ordinal);

        public override bool Equals(object obj) => Equals(obj as ClientPluginsState);

        public override int GetHashCode() => HashCode.Combine(Known, Enabled.Count, Disabled.Count);

        public override string ToString() => Describe();
    }
}
