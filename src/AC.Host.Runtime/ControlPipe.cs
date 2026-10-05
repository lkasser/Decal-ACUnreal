using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AC.Host.Plugins;

namespace AC.Host.Runtime
{
    /// <summary>
    /// Lets another process drive a running host: <c>achost ctl reload VirindiTank</c>,
    /// <c>achost ctl act on</c>. What Decal's window does, from a command line - so a plugin
    /// can be rebuilt and reloaded from a script without anyone reaching for the mouse.
    /// </summary>
    /// <remarks>
    /// A named pipe, so only this machine's user can reach it. One line in, text back, then
    /// the connection closes. Every command runs on the host's game thread, like a click in
    /// Decal's window. Whichever program runs the host serves it - achost or the Decal Agent -
    /// so <c>achost ctl</c> drives either.
    /// </remarks>
    public sealed class ControlPipe : IAsyncDisposable
    {
        public const string DefaultName = "achost-control";

        /// <summary>
        /// The pipe to use: ACHOST_CONTROL_PIPE when set, so a second host - one started to test
        /// something - and the `ctl` aimed at it can stay clear of the one the player is using.
        /// </summary>
        public static string ConfiguredName
            => Environment.GetEnvironmentVariable("ACHOST_CONTROL_PIPE") is string name && name.Length > 0 ? name : DefaultName;

        private readonly GameHost _host;
        private readonly PluginManager _plugins;
        private readonly string _name;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private Task _loop;

        public ControlPipe(GameHost host, PluginManager plugins, string name = DefaultName)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _plugins = plugins;
            _name = name;

            // The game's chat as the host read it, so `ctl chat` can say what the game said.
            _host.ChatReceived += (_, message) =>
            {
                lock (_chat)
                {
                    _chat.Enqueue(DateTime.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + "  " + message);
                    while (_chat.Count > ChatKept)
                        _chat.Dequeue();
                }
            };
        }

        /// <summary>
        /// Someone asked the host to exit (`ctl exit`). Whoever started the host decides what that
        /// means: Decal Agent exits as its Exit does, saving every plugin's settings. Raised on a
        /// thread of its own once the answer has been sent, never on the game thread.
        /// </summary>
        public event EventHandler ExitRequested;

        private const int ChatKept = 300;
        private readonly Queue<string> _chat = new Queue<string>();

        public string Name => _name;

        public void Start() => _loop = Task.Run(() => ServeAsync(_stop.Token));

        private async Task ServeAsync(CancellationToken stop)
        {
            while (!stop.IsCancellationRequested)
            {
                NamedPipeServerStream opened;
                try
                {
                    opened = new NamedPipeServerStream(_name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // Another host already serves this name. Trying again at once would spin a
                    // core for as long as that one runs, so wait, and take the name when it goes.
                    try
                    {
                        await Task.Delay(1000, stop).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    continue;
                }

                try
                {
                    using NamedPipeServerStream pipe = opened;
                    await pipe.WaitForConnectionAsync(stop).ConfigureAwait(false);

                    using StreamReader reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);

                    string line = await reader.ReadLineAsync(stop).ConfigureAwait(false);
                    string reply = await RunAsync(line ?? string.Empty).ConfigureAwait(false);
                    await pipe.WriteAsync(new UTF8Encoding(false).GetBytes(reply), stop).ConfigureAwait(false);
                    pipe.WaitForPipeDrain();
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (IOException)
                {
                    // The other end went away mid-command; wait for the next.
                }
                catch (TimeoutException)
                {
                    // The game thread did not answer in time. The asker gives up by itself; the
                    // pipe must not stop serving everyone after it.
                }
            }
        }

        /// <summary>Runs one command on the game thread and returns what to print.</summary>
        internal Task<string> RunAsync(string line)
        {
            TaskCompletionSource<string> done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _host.RunOnGameThread(() =>
            {
                try
                {
                    done.TrySetResult(Execute(line.Trim()));
                }
                catch (Exception ex)
                {
                    done.TrySetResult("failed: " + ex.Message);
                }
            });

            return done.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }

        /// <summary>Game thread only.</summary>
        private string Execute(string line)
        {
            string[] words = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string verb = words.Length > 0 ? words[0].ToLowerInvariant() : string.Empty;
            string rest = words.Length > 1 ? words[1] : string.Empty;

            switch (verb)
            {
                case "plugins":
                case "status":
                    return Status();

                case "act":
                    if (rest is "on" or "off")
                    {
                        if (!_host.CanAct)
                            return "this session cannot act";
                        _host.ActionsAllowed = rest == "on";
                    }

                    return "acting " + (_host.ActionsAllowed && _host.CanAct ? "allowed" : "off");

                case "reload":
                case "enable":
                case "disable":
                    // A Decal plugin by the name DecalCompat lists it under, when no plugin of the
                    // host's own is called that.
                    if (rest.Length > 0 && rest != "*" && (_plugins == null || !_plugins.Entries.Any(e => string.Equals(e.Name, rest, StringComparison.OrdinalIgnoreCase)))
                        && HostedNamed(rest) is IHostedPlugins hosting)
                    {
                        if (verb == "reload")
                            hosting.ReloadHosted(rest);
                        else
                            hosting.SetHostedEnabled(rest, verb == "enable");
                        return Status();
                    }

                    if (_plugins == null)
                        return "this host was started without plugins";

                    List<PluginEntry> targets = rest == "*" || (verb == "reload" && rest.Length == 0)
                        ? _plugins.Entries.ToList()
                        : _plugins.Entries.Where(e => string.Equals(e.Name, rest, StringComparison.OrdinalIgnoreCase)).ToList();

                    if (verb == "reload" && rest.Length == 0)
                        targets = targets.Where(e => e.Enabled).ToList();

                    if (targets.Count == 0)
                    {
                        _plugins.Rescan();
                        targets = _plugins.Entries.Where(e => string.Equals(e.Name, rest, StringComparison.OrdinalIgnoreCase)).ToList();
                        if (targets.Count == 0)
                            return $"no plugin named '{rest}'";
                        if (verb == "reload")
                            return Status();
                    }

                    foreach (PluginEntry entry in targets)
                    {
                        if (verb == "reload")
                            _plugins.Reload(entry);
                        else
                            _plugins.SetEnabled(entry, verb == "enable");
                    }

                    return Status();

                case "rescan":
                    _plugins?.Rescan();
                    foreach (IHostedPlugins runner in _host.Plugins.OfType<IHostedPlugins>())
                        runner.RescanHosted();
                    return Status();

                case "find":
                    return Find(rest);

                case "say":
                    return Say(rest);

                case "hotkey":
                    return Hotkey(rest);

                case "chat":
                    return Chat(rest);

                case "view":
                    return View(rest);

                case "windows":
                    return Windows();

                case "set":
                case "press":
                case "click":
                case "page":
                    return Operate(verb, rest);

                case "exit":
                    return Exit();

                default:
                    return "commands: status | plugins | act [on|off] | reload [name|*] | enable <name> | disable <name> | rescan" + Environment.NewLine
                        + "          find <name> | chat [lines] | windows | view <plugin>[/<window>] [filter]" + Environment.NewLine
                        + "          say <line> | hotkey <plugin> <id> | exit" + Environment.NewLine
                        + "          set <plugin> <control> <value> | press <plugin> <control> | click <plugin> <list> <row> <column> | page <plugin> <notebook> <page>";
            }
        }

        /// <summary>
        /// The objects whose names contain <paramref name="text"/>, as the host knows them: where
        /// each is, and who holds or wields it. For answering "does the host think I am holding
        /// that?" while the game runs.
        /// </summary>
        /// <summary>
        /// Asks whoever started the host to stop it. The answer goes back first: the stopping
        /// closes this very pipe, and waits for the game thread this runs on.
        /// </summary>
        private string Exit()
        {
            EventHandler exit = ExitRequested;
            if (exit == null)
                return "this host cannot be told to exit";

            _ = Task.Run(async () =>
            {
                await Task.Delay(250).ConfigureAwait(false);
                exit(this, EventArgs.Empty);
            });
            return "exiting";
        }

        private string Find(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "find what? Give part of a name.";

            uint me = _host.Character.Id;
            StringBuilder found = new StringBuilder();
            int count = 0;
            foreach (World.WorldObject obj in _host.World.Objects)
            {
                if (obj.Name == null || obj.Name.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                if (++count > 40)
                {
                    found.AppendLine("... and more");
                    break;
                }

                string where = obj.WielderId.HasValue
                    ? (obj.WielderId == me ? "wielded by you" : $"wielded by 0x{obj.WielderId:X8}")
                        + (obj.CurrentlyWieldedLocation.HasValue ? $" (slot 0x{obj.CurrentlyWieldedLocation:X})" : string.Empty)
                    : obj.ContainerId.HasValue
                        ? (obj.ContainerId == me ? "in your main pack" : $"in 0x{obj.ContainerId:X8}")
                        : obj.Location.HasValue ? $"on the ground at {obj.Location}" : "nowhere known";

                found.AppendLine($"0x{obj.Id:X8}  {obj.Name,-40} {where}");
            }

            return count == 0 ? $"nothing named like \"{text}\"" : found.ToString();
        }

        /// <summary>
        /// Runs a line as though the player had typed it in the game's chat - "/vt opt set
        /// enablecombat false", "@acehelp" - by the same route a typed line takes: offered to the
        /// plugins first, then sent as the client would. Says what became of it.
        /// </summary>
        private string Say(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return "say what?";

            // Commands run on the game thread already, which is where a typed line is run.
            return _host.RunChatCommand(line.Trim()).ToString();
        }

        /// <summary>A plugin's hotkey pressed, as the overlay sends one when its key goes down.</summary>
        private string Hotkey(string rest)
        {
            string[] words = SplitWindow(rest, 1);
            if (words.Length < 2)
                return "hotkey needs a plugin and the hotkey's id - Decal's hotkey windows list them";

            if (FindView(words[0], out _, out string owner) == null)
                owner = _host.Plugins.FirstOrDefault(p => string.Equals(p.Name, words[0], StringComparison.OrdinalIgnoreCase))?.Name;
            if (owner == null)
                return $"no plugin named '{words[0]}'";

            _host.DispatchCommand(owner, new OverlayCommand("hotkey", words[1]));
            return $"pressed {owner}'s hotkey {words[1]}";
        }

        /// <summary>The last lines of the game's chat the host has read, oldest first.</summary>
        private string Chat(string rest)
        {
            int lines = int.TryParse(rest, out int asked) && asked > 0 ? asked : 30;
            lock (_chat)
            {
                return _chat.Count == 0
                    ? "no chat yet"
                    : string.Join(Environment.NewLine, _chat.Skip(Math.Max(0, _chat.Count - lines))) + Environment.NewLine;
            }
        }

        /// <summary>A plugin's window as the player sees it: every control, and what it shows.</summary>
        private string View(string rest)
        {
            string[] words = SplitWindow(rest, 1);
            if (words.Length == 0)
                return "view which plugin?";

            Plugins.Views.DecalView view = FindView(words[0], out string refusal, out _);
            if (view == null)
                return refusal;

            string filter = words.Length > 1 ? words[1] : null;
            StringBuilder text = new StringBuilder();
            text.AppendLine(view.Title);
            foreach (Plugins.Views.ViewControl control in view.Controls)
            {
                if (string.IsNullOrEmpty(control.Name))
                    continue;

                string shown = control switch
                {
                    Plugins.Views.Checkbox box => (box.Checked ? "[x] " : "[ ] ") + box.Text,
                    Plugins.Views.Edit edit => "\"" + edit.Text + "\"",
                    Plugins.Views.Choice choice => $"{choice.Selected}: {choice.SelectedText}",
                    Plugins.Views.Slider slider => slider.Position.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
                    Plugins.Views.StaticText label => label.Text,
                    Plugins.Views.PushButton button => "(" + button.Text + ")",
                    Plugins.Views.Notebook notebook => "page " + notebook.ActivePage,
                    Plugins.Views.List list => ListText(list),
                    _ => null,
                };

                if (shown == null)
                    continue;

                // A list is matched by its name alone: its rows would match almost anything.
                if (filter != null && control.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0
                    && (control is Plugins.Views.List || shown.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0))
                    continue;

                text.AppendLine($"{control.Name,-28} {shown}");
            }

            return text.ToString();
        }

        private static string ListText(Plugins.Views.List list)
        {
            StringBuilder rows = new StringBuilder($"{list.RowCount} rows");
            for (int r = 0; r < list.RowCount && r < 60; r++)
            {
                rows.AppendLine();
                rows.Append($"      {r,3}:");
                foreach (Plugins.Views.ListCell cell in list[r].Cells)
                    rows.Append(" | " + (string.IsNullOrEmpty(cell.Text) ? (cell.Checked ? "x" : ".") : cell.Text));
            }

            return rows.ToString();
        }

        /// <summary>
        /// The window a command names, then up to <paramref name="more"/> further words, the last
        /// holding the rest of the line. A window's name may have spaces in it - "DecalCompat/Virindi
        /// Reporter" - so the longest name of a window or plugin that begins the line is taken
        /// first; failing that, the first word.
        /// </summary>
        private string[] SplitWindow(string rest, int more)
            => SplitWindow(rest, more, _host.CollectWindows().Select(w => w.Owner).Concat(_host.Plugins.Select(p => p.Name)));

        internal static string[] SplitWindow(string rest, int more, IEnumerable<string> names)
        {
            rest = (rest ?? string.Empty).Trim();
            if (rest.Length == 0)
                return Array.Empty<string>();

            string window = null;
            foreach (string name in names)
            {
                if (string.IsNullOrEmpty(name) || (window != null && name.Length <= window.Length))
                    continue;
                if (rest.Equals(name, StringComparison.OrdinalIgnoreCase)
                    || (rest.Length > name.Length && rest.StartsWith(name, StringComparison.OrdinalIgnoreCase) && rest[name.Length] == ' '))
                    window = rest.Substring(0, name.Length);
            }

            window ??= rest.Split(' ', 2)[0];
            string remainder = rest.Substring(window.Length).Trim();
            List<string> words = new List<string> { window };
            if (remainder.Length > 0)
                words.AddRange(remainder.Split(' ', more, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            return words.ToArray();
        }

        /// <summary>Every window the host would draw, by the name view, set and press take.</summary>
        private string Windows()
        {
            StringBuilder text = new StringBuilder();
            foreach (OverlayWindowInfo window in _host.CollectWindows())
            {
                if (window.View != null)
                    text.AppendLine($"{window.Owner,-40} {window.View.Title}");
            }

            return text.Length == 0 ? "no windows" : text.ToString();
        }

        /// <summary>
        /// A plugin's window by its name, or one of the windows it hosts by "Plugin/key" - the
        /// Decal Agent's "Decal/vhs", Virindi Tank's "VirindiTank/status" - as the overlay names them.
        /// </summary>
        private Plugins.Views.DecalView FindView(string plugin, out string refusal, out string owner)
        {
            refusal = null;
            owner = plugin;
            int slash = plugin.IndexOf('/');
            if (slash > 0)
            {
                foreach (OverlayWindowInfo window in _host.CollectWindows())
                {
                    if (string.Equals(window.Owner, plugin, StringComparison.OrdinalIgnoreCase) && window.View != null)
                    {
                        owner = window.Owner;
                        return window.View;
                    }
                }

                refusal = $"no window named '{plugin}'; `windows` lists them";
                return null;
            }

            foreach (IPlugin candidate in _host.Plugins)
            {
                if (!string.Equals(candidate.Name, plugin, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (candidate is not IOverlayView viewer || viewer.View == null)
                {
                    refusal = $"{candidate.Name} has no window";
                    return null;
                }

                owner = candidate.Name;
                return viewer.View;
            }

            refusal = $"no plugin named '{plugin}'";
            return null;
        }

        /// <summary>
        /// Does to a plugin's window what a click in the game does: sends it the same command,
        /// by the same route, so the plugin cannot tell the difference.
        /// </summary>
        private string Operate(string verb, string rest)
        {
            string[] words = SplitWindow(rest, verb == "set" ? 2 : 3);
            if (words.Length < 2)
                return $"{verb} needs a plugin and a control";

            string plugin = words[0];
            string control = words[1];
            if (FindView(plugin, out string refusal, out string owner) is not Plugins.Views.DecalView view)
                return refusal;
            if (!view.Contains(control))
                return $"{plugin} has no control named '{control}'";

            OverlayCommand command;
            switch (verb)
            {
                case "set":
                    if (words.Length < 3)
                        return "set needs a value";
                    command = new OverlayCommand("set", words[2], controlId: control);
                    break;
                case "press":
                    command = new OverlayCommand("press", controlId: control);
                    break;
                case "page":
                    if (words.Length < 3)
                        return "page needs a page number";
                    command = new OverlayCommand("page", words[2], controlId: control);
                    break;
                default:
                    if (words.Length < 4)
                        return "click needs a row and a column";
                    command = new OverlayCommand("click", words[3], rowId: words[2], controlId: control);
                    break;
            }

            // Queued behind this command, on the game thread, exactly as the overlay's are.
            _host.DispatchCommand(owner, command);
            return $"sent {command} to {owner}";
        }

        /// <summary>The plugin that runs a plugin of this name - DecalCompat, for a Decal plugin - or null.</summary>
        private IHostedPlugins HostedNamed(string name)
        {
            foreach (IHostedPlugins hosting in _host.Plugins.OfType<IHostedPlugins>())
            {
                try
                {
                    if (hosting.HostedPlugins.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                        return hosting;
                }
                catch (Exception)
                {
                    // Its list is its business; Status says the same when it lists nothing.
                }
            }

            return null;
        }

        private string Status()
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine($"server     {(string.IsNullOrEmpty(_host.World.ServerName) ? "(not connected)" : _host.World.ServerName)}");
            text.AppendLine($"character  {(_host.Character.Id == 0 ? "(not logged in)" : _host.Character.Name)}");

            // A host started while the game was connected: carried on from the one before it, or
            // knowing only what it has seen since - in which case the player has been asked to help.
            if (_host.JoinedMidSession)
                text.AppendLine("session    joined while you were in the world: log out to character select and enter the world again so the host can see your character");
            else if (_host.CarriedOnFrom is DateTimeOffset carriedOn)
                text.AppendLine($"session    carried on from the host that stopped at {carriedOn.ToLocalTime():HH:mm:ss}");
            text.AppendLine($"acting     {(!_host.CanAct ? "impossible (replay)" : _host.ActionsAllowed ? "allowed" : "off")}");

            // The answers to the host's own appraisals, kept from a client that would open its
            // examine panel for each - counted, so that working can be seen.
            if (_host.AppraisalsWithheld is (int answers, int split))
                text.AppendLine($"appraisals {answers} withheld from the client ({split} in several fragments)");

            // The host's plugins, each followed by the plugins it runs - Decal's, under DecalCompat.
            foreach (PluginListRow row in PluginList.Build(_plugins?.Entries, _host.Plugins))
            {
                text.AppendLine(row.Hosted
                    ? $"  decal    {row.Name,-26} {(row.Enabled ? "on " : "off")}  {row.Version,-10} {row.Status}"
                    : $"plugin     {row.Name,-16} {(row.Enabled ? "on " : "off")}  {row.Version,-10} {row.Status}");
            }

            return text.ToString();
        }

        /// <summary>Sends one command to a running host's pipe and returns its reply.</summary>
        public static async Task<string> SendAsync(string command, string name = DefaultName, int timeoutMs = 30000)
        {
            using NamedPipeClientStream pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(Math.Min(timeoutMs, 5000)).ConfigureAwait(false);

            // Raw bytes rather than a writer: a writer flushes when it is disposed, and by then
            // the host has answered and closed its end, so the flush fails on a broken pipe.
            byte[] request = new UTF8Encoding(false).GetBytes(command + Environment.NewLine);
            await pipe.WriteAsync(request).ConfigureAwait(false);

            using StreamReader reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
            using CancellationTokenSource timeout = new CancellationTokenSource(timeoutMs);
            return await reader.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            if (_loop != null)
            {
                try
                {
                    await _loop.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            _stop.Dispose();
        }
    }
}
