using System;
using System.Collections.Generic;
using System.Linq;
using AC.Host.Actions;
using AC.Host.World;

namespace AC.Host.Plugins
{
    /// <summary>
    /// The host's stand-in for the game's chat box: lines run as though typed
    /// (<see cref="IHost.RunChatCommand"/>), and lines the player typed that belong to a plugin.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A line goes where it went with Decal: to the plugins first, any of which may take it as
    /// its own command, then to the game client, which sends the server what its command asks
    /// for. Virindi Tank's meta sent every Chat Command action down that path, so "/mt use
    /// Dwennon" reached Mag-Tools and "/f !start" reached the fellowship.
    /// </para>
    /// <para>
    /// Lines the player types are kept from the server by the relay when their first word is a
    /// plugin's (<see cref="IChatCommands.CommandWords"/>), plus any the player lists in the
    /// setting "Decal:CommandWords" - for Decal plugins, which never said which words were
    /// theirs. The list is kept as one array swapped whole, because the relay reads it on its
    /// own thread while the game thread rebuilds it.
    /// </para>
    /// </remarks>
    internal sealed class ChatBox
    {
        private readonly GameHost _host;
        private readonly WorldState _world;
        private readonly IPluginLog _log;
        private readonly string[] _configuredWords;
        private volatile string[] _words = Array.Empty<string>();
        private uint _cookie;

        internal ChatBox(GameHost host, WorldState world, IPluginLog log, IReadOnlyDictionary<string, string> settings)
        {
            _host = host;
            _world = world;
            _log = log;
            _configuredWords = settings != null && settings.TryGetValue("Decal:CommandWords", out string listed) && !string.IsNullOrWhiteSpace(listed)
                ? listed.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(Word).Where(w => w.Length > 0).ToArray()
                : Array.Empty<string>();
            _words = _configuredWords;
        }

        /// <summary>A command word as it is matched: no slash or at sign, no spaces round it.</summary>
        private static string Word(string word) => (word ?? string.Empty).Trim().TrimStart('/', '@').Trim();

        /// <summary>The words whose lines are kept from the server, as the relay sees them now.</summary>
        internal IReadOnlyCollection<string> CommandWords => _words;

        /// <summary>Collects the plugins' command words again. Game thread only.</summary>
        internal void RefreshCommandWords(IEnumerable<IPlugin> plugins)
        {
            HashSet<string> words = new HashSet<string>(_configuredWords, StringComparer.OrdinalIgnoreCase);
            foreach (IPlugin plugin in plugins.ToList())
            {
                if (plugin is not IChatCommands commands)
                    continue;

                try
                {
                    foreach (string word in commands.CommandWords ?? Array.Empty<string>())
                    {
                        // A word of nothing but "/" would claim every slash command there is.
                        string bare = Word(word);
                        if (bare.Length > 0)
                            words.Add(bare);
                    }
                }
                catch (Exception ex)
                {
                    _host.Statistics.PluginExceptions++;
                    _log.Error($"Plugin {plugin.Name} threw while naming its commands.", ex);
                }
            }

            _words = words.ToArray();
        }

        /// <summary>
        /// On the relay's thread: whether a typed line is a plugin's - "/" or "@", then one of the
        /// words, then a space or nothing. Must stay quick and must not touch a plugin.
        /// </summary>
        internal bool IsPluginCommandLine(string line)
        {
            if (string.IsNullOrEmpty(line) || (line[0] != '/' && line[0] != '@'))
                return false;

            string[] words = _words;
            foreach (string word in words)
            {
                if (line.Length > word.Length
                    && string.Compare(line, 1, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) == 0
                    && (line.Length == word.Length + 1 || line[word.Length + 1] == ' '))
                    return true;
            }

            return false;
        }

        /// <summary>A line the player typed for a plugin, kept from the server. Game thread.</summary>
        internal void RunTyped(string line)
        {
            try
            {
                if (_host.DispatchChatCommand(line))
                    return;

                // Kept from the server already; saying nothing would lose it silently.
                _log.Warn($"\"{line}\" was typed for a plugin, and no plugin took it; it was not sent to the server either.");
                _host.ShowInGame($"No plugin took \"{line}\", and it was not said aloud.");
            }
            finally
            {
                _host.Statistics.TypedCommands++;
            }
        }

        /// <summary>Runs a line as though typed. Game thread only.</summary>
        internal ChatCommandOutcome Run(string text, IPlugin from)
        {
            if (string.IsNullOrWhiteSpace(text))
                return ChatCommandOutcome.Empty;

            if (_host.DispatchChatCommand(text, from))
                return ChatCommandOutcome.Plugin;

            ClientCommand command = ClientCommands.Parse(text, new ClientCommandContext
            {
                PlayerId = _world.Character.Id,
                LastTellFrom = _world.LastTellFrom,
                LastTellTo = _world.LastTellTo,
                Rooms = _world.TurbineChannels,
                Cookie = ++_cookie,
            });

            switch (command.Kind)
            {
                case ClientCommandKind.ClientOnly:
                    return ChatCommandOutcome.ClientOnly;

                case ClientCommandKind.Unknown:
                    return ChatCommandOutcome.Unknown;

                case ClientCommandKind.NotSent:
                    _log.Info($"\"{text}\" was not sent: {command.Reason}");
                    return ChatCommandOutcome.NotSent;
            }

            if (_host.Actions is not ClientActions actions || !actions.IsAvailable)
                return ChatCommandOutcome.NotSent;

            if (command.TellTarget != null)
                _world.LastTellTo = command.TellTarget;

            _ = actions.SendCommandAsync(command);
            return ChatCommandOutcome.Sent;
        }
    }
}
