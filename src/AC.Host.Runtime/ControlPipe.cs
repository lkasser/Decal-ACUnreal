using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AC.Host.Actions;
using AC.Host.Plugins;
using AC.Host.World;

namespace AC.Host.Runtime
{
    /// <summary>
    /// Lets another process drive a running host: <c>achost ctl reload VirindiTank</c>,
    /// <c>achost ctl act on</c>. What Decal's window does, from a command line - so a plugin
    /// can be rebuilt and reloaded from a script without anyone reaching for the mouse.
    /// </summary>
    /// <remarks>
    /// A named pipe, so only this machine's user can reach it. One line in, text back, then
    /// the connection closes; several askers are answered at once, so one waiting on a logout
    /// does not keep the rest out. Every command runs on the host's game thread, like a click in
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

        /// <summary>
        /// How many askers are answered at once. A `ctl logout` waits up to <see cref="SessionWait"/>
        /// for its answer, and whatever else is asked meanwhile - a script polling where the
        /// character is - is answered beside it, not told there is no host.
        /// </summary>
        public const int AnsweredAtOnce = 8;

        /// <summary>The instances of the pipe this host has open, listening or answering.</summary>
        private int _open;

        private async Task ServeAsync(CancellationToken stop)
        {
            using SemaphoreSlim room = new SemaphoreSlim(AnsweredAtOnce);
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await room.WaitAsync(stop).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                NamedPipeServerStream opened;
                try
                {
                    // The first instance this host opens claims the name, and fails if another host
                    // already has it; the rest are opened beside it while it is still held.
                    PipeOptions options = PipeOptions.Asynchronous | (Volatile.Read(ref _open) == 0 ? PipeOptions.FirstPipeInstance : PipeOptions.None);
                    opened = new NamedPipeServerStream(_name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, options);
                    Interlocked.Increment(ref _open);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // Another host already serves this name. Trying again at once would spin a
                    // core for as long as that one runs, so wait, and take the name when it goes.
                    room.Release();
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
                    await opened.WaitForConnectionAsync(stop).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException || ex is IOException)
                {
                    Close(opened, room);
                    if (ex is OperationCanceledException)
                        return;
                    continue;
                }

                // Answered beside the wait for the next asker, which begins at once.
                _ = Task.Run(() => AnswerAsync(opened, room, stop));
            }
        }

        /// <summary>One asker: its line read, run on the game thread, and the answer written back.</summary>
        private async Task AnswerAsync(NamedPipeServerStream pipe, SemaphoreSlim room, CancellationToken stop)
        {
            try
            {
                using StreamReader reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);

                string line = await reader.ReadLineAsync(stop).ConfigureAwait(false);
                string reply = await RunAsync(line ?? string.Empty).ConfigureAwait(false);
                await pipe.WriteAsync(new UTF8Encoding(false).GetBytes(reply), stop).ConfigureAwait(false);
                pipe.WaitForPipeDrain();
            }
            catch (OperationCanceledException)
            {
                // The host is stopping.
            }
            catch (IOException)
            {
                // The other end went away mid-command.
            }
            catch (TimeoutException)
            {
                // The game thread did not answer in time. The asker gives up by itself; the
                // pipe must not stop serving everyone after it.
            }
            finally
            {
                Close(pipe, room);
            }
        }

        private void Close(NamedPipeServerStream pipe, SemaphoreSlim room)
        {
            pipe.Dispose();
            Interlocked.Decrement(ref _open);
            try
            {
                room.Release();
            }
            catch (ObjectDisposedException)
            {
                // The host stopped serving while this asker was answered.
            }
        }

        /// <summary>
        /// How long <c>ctl logout</c> and <c>ctl login</c> wait for how it ends before answering
        /// that it is still under way: inside the asker's thirty seconds, and longer than a logout
        /// takes - the server's six, the client's three to settle, three more if its key did nothing.
        /// </summary>
        public static readonly TimeSpan SessionWait = TimeSpan.FromSeconds(25);

        /// <summary>Runs one command on the game thread and returns what to print.</summary>
        internal async Task<string> RunAsync(string line)
        {
            TaskCompletionSource<Task<string>> started = new TaskCompletionSource<Task<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
            _host.RunOnGameThread(() =>
            {
                try
                {
                    started.TrySetResult(ExecuteAsync(line.Trim()));
                }
                catch (Exception ex)
                {
                    started.TrySetResult(Task.FromResult("failed: " + ex.Message));
                }
            });

            // A logout or a login is started on the game thread and then waited for off it, so the
            // game goes on while it does.
            Task<string> answer = await started.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            return await answer.ConfigureAwait(false);
        }

        /// <summary>Game thread only: what a command answers, now or once it has run its course.</summary>
        private Task<string> ExecuteAsync(string line)
        {
            string[] words = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string verb = words.Length > 0 ? words[0].ToLowerInvariant() : string.Empty;
            string rest = words.Length > 1 ? words[1] : string.Empty;

            return verb switch
            {
                "logout" => LogOut(rest),
                "login" => LogIn(rest),
                _ => Task.FromResult(Execute(line)),
            };
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

                case "characters":
                    return Characters();

                case "window":
                    return GameWindow(rest);

                case "set":
                case "press":
                case "click":
                case "page":
                    return Operate(verb, rest);

                case "exit":
                    return Exit();

                default:
                    return "commands: status | plugins | act [on|off] | reload [name|*] | enable <name> | disable <name> | rescan" + Environment.NewLine
                        + "          find <name> | chat [lines] | windows | view <plugin>[/<window>] [filter] | window [keep on|off]" + Environment.NewLine
                        + "          say <line> | hotkey <plugin> <id> | exit" + Environment.NewLine
                        + "          characters | logout [keys|messages] | login <name|id|number> [keys|messages]" + Environment.NewLine
                        + "          set <plugin> <control> <value> | press <plugin> <control> | click <plugin> <list> <row> <column> | page <plugin> <notebook> <page>";
            }
        }

        // ------------------------------------------------------------------- the character list

        /// <summary>
        /// Logs the character out to the character list, and answers how that ended - or that it is
        /// still under way, after <see cref="SessionWait"/>. Always by the message: "keys" is read,
        /// and the log says why it cannot be had (<see cref="SessionRoute.Keys"/>). Game thread
        /// only, up to the wait.
        /// </summary>
        private Task<string> LogOut(string rest)
        {
            if (_host.Session == null)
                return Task.FromResult("this host cannot act, so it cannot log out");

            if (!TryReadRoute(rest.Trim(), out SessionRoute route, out string unread) || unread.Length > 0)
                return Task.FromResult("logout takes nothing, or keys or messages");

            SessionRequest request = _host.Session.BeginLogOut(route, out string refusal);
            return request == null ? Task.FromResult("cannot log out: " + refusal) : AwaitAsync(request);
        }

        /// <summary>
        /// Enters the world as one of the account's characters - by its name, its id or its number
        /// in `ctl characters` - and answers how that ended, or that it is still under way. Always
        /// by the character select's own Enter, clicked: a last word "messages" is read, and the
        /// log says why it cannot be had (<see cref="SessionRoute.Messages"/>). Game thread only,
        /// up to the wait.
        /// </summary>
        private Task<string> LogIn(string rest)
        {
            if (_host.Session == null)
                return Task.FromResult("this host cannot act, so it cannot enter the world");

            TryReadRoute(rest.Trim(), out SessionRoute route, out string named);
            if (named.Length == 0)
                return Task.FromResult("login which character? `characters` lists them");

            AccountCharacter character = AccountCharacter.Find(_host.World.AccountCharacters, named);
            if (character == null)
            {
                return Task.FromResult(_host.World.AccountCharacters.Count == 0
                    ? "no characters are known: the character list has not come since the host started"
                    : $"no one character is \"{named}\"; `characters` lists them");
            }

            SessionRequest request = _host.Session.BeginEnterWorld(character.Id, route, out string refusal);
            return request == null ? Task.FromResult("cannot enter the world: " + refusal) : AwaitAsync(request);
        }

        /// <summary>
        /// The way a logout or a login is to go, read off the end of the line: "keys", "messages", or
        /// "auto"; Auto when the line ends otherwise. <paramref name="rest"/> is what comes before it.
        /// </summary>
        internal static bool TryReadRoute(string line, out SessionRoute route, out string rest)
        {
            route = SessionRoute.Auto;
            rest = line ?? string.Empty;

            int space = rest.LastIndexOf(' ');
            string last = space < 0 ? rest : rest.Substring(space + 1);
            SessionRoute? named = last.ToLowerInvariant() switch
            {
                "keys" => SessionRoute.Keys,
                "messages" => SessionRoute.Messages,
                "auto" => SessionRoute.Auto,
                _ => null,
            };

            if (named == null)
                return rest.Length == 0;

            route = named.Value;
            rest = space < 0 ? string.Empty : rest.Substring(0, space).Trim();
            return true;
        }

        /// <summary>How a logout or a login ended, or where it stands after <see cref="SessionWait"/>.</summary>
        private static async Task<string> AwaitAsync(SessionRequest request)
        {
            Task done = await Task.WhenAny(request.Finished, Task.Delay(SessionWait)).ConfigureAwait(false);
            return done == request.Finished
                ? request.Outcome
                : $"still under way after {SessionWait.TotalSeconds:0} s: {request}. `status` says how it ends.";
        }

        /// <summary>The account's characters as the character list gave them, numbered as `login` takes them.</summary>
        private string Characters()
        {
            IReadOnlyList<AccountCharacter> characters = _host.World.AccountCharacters;
            if (characters.Count == 0)
                return "no characters are known: the character list has not come since the host started";

            StringBuilder text = new StringBuilder();
            text.AppendLine($"account    {_host.World.AccountName}");
            for (int i = 0; i < characters.Count; i++)
            {
                AccountCharacter character = characters[i];
                string state = character.Id == _host.Character.Id && _host.Character.Id != 0
                    ? "in the world"
                    : character.DeleteTimeout != 0 ? $"being deleted, {character.DeleteTimeout} s left" : string.Empty;
                text.AppendLine($"{i + 1,3}  0x{character.Id:X8}  {character.Name,-24} {state}".TrimEnd());
            }

            return text.ToString();
        }

        /// <summary>Where the session stands, for `status`.</summary>
        private string SessionLine()
        {
            IWorldView world = _host.World;
            string who = string.IsNullOrEmpty(_host.Character.Name) ? $"0x{_host.Character.Id:X8}" : _host.Character.Name;
            return world.Phase switch
            {
                SessionPhase.CharacterList => $"at the character list (account {world.AccountName}, {world.AccountCharacters.Count} character(s))",
                SessionPhase.EnteringWorld => _host.WorldState.EnteringCharacterId != 0
                    ? $"entering the world as 0x{_host.WorldState.EnteringCharacterId:X8}"
                    : "entering the world",
                SessionPhase.InWorld => _host.Character.Id != 0 ? "in the world as " + who : "in the world (the character not yet known)",
                SessionPhase.LoggingOff => "logging off " + who,
                _ => "no session seen",
            };
        }

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

        /// <summary>
        /// The objects whose names contain <paramref name="text"/>, as the host knows them: where
        /// each is, and who holds or wields it. For answering "does the host think I am holding
        /// that?" while the game runs.
        /// </summary>
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
            text.AppendLine($"phase      {SessionLine()}");

            // A logout or a login under way, or how the last one ended; and the client's own screen,
            // when its log can be read.
            if (_host.Session?.Describe() is string relog)
                text.AppendLine($"relog      {relog}");
            if (_host.Session != null && _host.Session.Screen != ClientScreen.Unknown)
                text.AppendLine($"client     shows {SessionControl.Describe(_host.Session.Screen)}, by its log");

            // A host started while the game was connected: carried on from the one before it, or
            // knowing only what it has seen since - in which case the player has been asked to help.
            if (_host.JoinedMidSession)
                text.AppendLine("session    joined while you were in the world: log out to character select and enter the world again so the host can see your character");
            else if (_host.CarriedOnFrom is DateTimeOffset carriedOn)
                text.AppendLine($"session    carried on from the host that stopped at {carriedOn.ToLocalTime():HH:mm:ss}");

            // Where the host looked for a session handed over when it started, what it found, and
            // what became of it: the whole story of a handover that did or did not happen.
            if (_host.HandoverLookup != null)
                text.AppendLine($"handover   {_host.HandoverLookup}{(_host.HandoverFate != null ? ": " + _host.HandoverFate : string.Empty)}");
            text.AppendLine($"acting     {(!_host.CanAct ? "impossible (replay)" : _host.ActionsAllowed ? "allowed" : "off")}");
            text.AppendLine("window     " + WindowLine());

            // AC:Unreal's own plugins, as its settings say: UCM, or another that can play the
            // character, enabled beside Virindi Tank. Enabled can be seen; running cannot.
            ClientPluginsState clientPlugins = _host.ClientPlugins ?? ClientPluginsState.Unknown;
            text.AppendLine("ac plugins " + clientPlugins.Describe()
                + (clientPlugins.AutomationEnabled
                    ? $" - {string.Join(" and ", clientPlugins.Automating.Select(p => p.Name))} can play the character: enabled, though whether it is running the client does not say"
                    : string.Empty));
            if (_host.ClientPluginBar != null)
                text.AppendLine($"ui scale   {ClientDisplay.Percent(_host.ClientUiScale)}, the client's Desktop UI Scale; its plugin bar at {_host.ClientPluginBar.Describe()}");

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

        /// <summary>
        /// The game window as the overlay last said, whether minimizing parks it, and whether the
        /// keys held now are going unheeded: one line, for status and for "window".
        /// </summary>
        private string WindowLine()
        {
            string line = _host.GameWindow.Describe()
                + "; keep playing while minimized " + (_host.KeepPlayingMinimized ? "on" : "off");

            IReadOnlyCollection<GameKey> held = _host.InputKeys.Held;
            if (held.Count > 0)
                line += "; holding " + string.Join(" ", held) + (_host.InputKeys.Unheeded ? ", which the game is not acting on" : string.Empty);

            return line;
        }

        /// <summary>"window" says what the game window is doing; "window keep on|off" switches parking it in place of minimizing.</summary>
        private string GameWindow(string rest)
        {
            string[] words = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 2 && words[0].Equals("keep", StringComparison.OrdinalIgnoreCase) && words[1] is "on" or "off")
                _host.KeepPlayingMinimized = words[1] == "on";
            else if (words.Length > 0)
                return "usage: window [keep on|off]";

            return WindowLine();
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
