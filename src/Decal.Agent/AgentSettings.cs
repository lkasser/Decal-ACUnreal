using System;
using System.IO;
using System.Text.Json;

namespace Decal.Agent
{
    /// <summary>What the Options dialog sets, kept between runs in DecalAgent.json in the host's data folder.</summary>
    /// <remarks>
    /// The movement keys are not here: Decal's window in the game binds them too, and they are
    /// kept once, in its own settings, so that neither window can disagree with the other.
    /// </remarks>
    internal sealed class AgentSettings
    {
        /// <summary>The game server the relay forwards to.</summary>
        public string ServerHost { get; set; } = "127.0.0.1";

        /// <summary>Its first port; ACE's is 9000.</summary>
        public int ServerPort { get; set; } = 9000;

        /// <summary>The first port the client is pointed at, on this machine.</summary>
        public int ListenPort { get; set; } = 9100;

        /// <summary>The folder holding client_portal.dat, or empty to look in the usual places.</summary>
        public string DatFolder { get; set; } = string.Empty;

        /// <summary>ACUnrealOverlay.dll, or empty to use the one beside the Agent or in the build folder.</summary>
        public string OverlayDll { get; set; } = string.Empty;

        /// <summary>Whether plugins may act from the moment the host starts.</summary>
        public bool ActAtStart { get; set; }

        /// <summary>Whether the overlay is put into AC:Unreal when it starts, without being asked.</summary>
        public bool AutoInject { get; set; } = true;

        /// <summary>The file these are kept in.</summary>
        public static string PathIn(string dataDirectory) => Path.Combine(dataDirectory, "DecalAgent.json");

        /// <summary>Reads the settings, or the defaults if there are none or they cannot be read.</summary>
        public static AgentSettings Load(string dataDirectory, Action<string> warn)
        {
            string path = PathIn(dataDirectory);
            try
            {
                if (File.Exists(path))
                    return JsonSerializer.Deserialize<AgentSettings>(File.ReadAllText(path)) ?? new AgentSettings();
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
            {
                warn?.Invoke($"Could not read the Agent's settings from {path}: {ex.Message}. Using the defaults.");
            }

            return new AgentSettings();
        }

        /// <summary>Writes the settings; false, with the reason given to <paramref name="warn"/>, if they could not be.</summary>
        public bool Save(string dataDirectory, Action<string> warn)
        {
            string path = PathIn(dataDirectory);
            try
            {
                Directory.CreateDirectory(dataDirectory);
                File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                warn?.Invoke($"Could not save the Agent's settings to {path}: {ex.Message}");
                return false;
            }
        }

        public AgentSettings Copy() => (AgentSettings)MemberwiseClone();
    }
}
