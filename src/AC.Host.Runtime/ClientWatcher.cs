using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace AC.Host.Runtime
{
    /// <summary>The game client as seen from outside: its process and its window's title.</summary>
    public readonly record struct ClientWindow(int ProcessId, string Title);

    /// <summary>What one attempt to put the overlay into the client came to.</summary>
    public sealed class ClientInjection
    {
        public ClientInjection(int processId, bool success, string message)
        {
            ProcessId = processId;
            Success = success;
            Message = message ?? string.Empty;
        }

        /// <summary>The client process it was aimed at; 0 when there was none.</summary>
        public int ProcessId { get; }

        /// <summary>True when the overlay is now in the client, whether put there now or already there.</summary>
        public bool Success { get; }

        /// <summary>What happened, in a sentence fit to show the player.</summary>
        public string Message { get; }

        public override string ToString() => Message;
    }

    /// <summary>
    /// Watches for the AC:Unreal client and puts the overlay into it: once per client process,
    /// once its window has been up for a moment, and never into anything else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Injecting into the wrong process, or twice into the right one, is the kind of mistake that
    /// closes a game mid-session, which is why <c>acinject</c> itself does nothing unless told
    /// exactly what to do. Doing it unasked is only acceptable under rules as narrow as these:
    /// </para>
    /// <list type="bullet">
    /// <item>The process is called ACUnreal <em>and</em> owns a window whose title begins
    /// "AC:Unreal". The client starts a launcher process under the same name, and that one has no
    /// window.</item>
    /// <item>The window has been there for <see cref="Settle"/>, so the game is past starting its
    /// renderer rather than in the middle of it.</item>
    /// <item>One attempt per process, whatever it came to. A failure is reported, not retried:
    /// if it failed for a reason that will not change, retrying only repeats the risk. The
    /// player can always ask again with <see cref="InjectNow"/>.</item>
    /// <item>One client. A second copy of the game would find the overlay's pipe taken by the
    /// first, so it is left alone.</item>
    /// </list>
    /// <para>
    /// The injecting itself is handed in, so this decides only when; it is called from whatever
    /// thread polls, and may block for as long as the client takes to load the library.
    /// </para>
    /// </remarks>
    public sealed class ClientWatcher
    {
        /// <summary>The client's executable name, without the extension.</summary>
        public const string ProcessName = "ACUnreal";

        /// <summary>How the game's own window's title begins.</summary>
        public const string WindowTitlePrefix = "AC:Unreal";

        private readonly object _gate = new object();
        private readonly Func<int, ClientInjection> _inject;
        private readonly Func<IReadOnlyList<ClientWindow>> _find;
        private readonly Dictionary<int, DateTime> _firstSeen = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, ClientInjection> _outcomes = new Dictionary<int, ClientInjection>();

        /// <param name="inject">Puts the overlay into the client with this process id, and says how it went.</param>
        /// <param name="find">Finds the running clients; <see cref="FindClients"/> when null.</param>
        public ClientWatcher(Func<int, ClientInjection> inject, Func<IReadOnlyList<ClientWindow>> find = null)
        {
            _inject = inject ?? throw new ArgumentNullException(nameof(inject));
            _find = find ?? FindClients;
        }

        /// <summary>Whether a client is injected into without being asked. On unless the player turned it off.</summary>
        public bool AutoInject { get; set; } = true;

        /// <summary>How long the client's window must have been up before it is injected into.</summary>
        public TimeSpan Settle { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>The client being watched, or null when none is running.</summary>
        public ClientWindow? Client
        {
            get { lock (_gate) return _client; }
        }

        private ClientWindow? _client;

        /// <summary>How many copies of the client are running; only the first is injected into.</summary>
        public int ClientCount
        {
            get { lock (_gate) return _clientCount; }
        }

        private int _clientCount;

        /// <summary>What the attempt on the current client came to, or null if none has been made.</summary>
        public ClientInjection Outcome
        {
            get
            {
                lock (_gate)
                    return _client.HasValue && _outcomes.TryGetValue(_client.Value.ProcessId, out ClientInjection outcome) ? outcome : null;
            }
        }

        /// <summary>Raised after every attempt, automatic or asked for, on the thread that made it.</summary>
        public event EventHandler<ClientInjection> Attempted;

        /// <summary>
        /// Looks for the client once, and injects into it if the rules say it is time. Call it
        /// every second or two.
        /// </summary>
        /// <returns>The attempt made, or null if none was.</returns>
        public ClientInjection Poll(DateTime now)
        {
            IReadOnlyList<ClientWindow> found = _find() ?? Array.Empty<ClientWindow>();
            int target;

            lock (_gate)
            {
                Track(found, now);

                if (!AutoInject || !_client.HasValue)
                    return null;

                target = _client.Value.ProcessId;
                if (_outcomes.ContainsKey(target) || now - _firstSeen[target] < Settle)
                    return null;

                // Claimed before the attempt, which can take seconds: another poll meanwhile must
                // not start a second one into the same process.
                _outcomes[target] = new ClientInjection(target, false, "Putting the overlay into AC:Unreal...");
            }

            return Attempt(target);
        }

        /// <summary>
        /// Injects into the current client now, whatever was tried before - the player asking.
        /// The injector itself still refuses a client that already has the overlay.
        /// </summary>
        public ClientInjection InjectNow()
        {
            // Asked for before the first look has found it: look now, rather than say it is not there.
            IReadOnlyList<ClientWindow> found = Client.HasValue ? null : _find() ?? Array.Empty<ClientWindow>();

            int target;
            lock (_gate)
            {
                if (found != null)
                    Track(found, DateTime.UtcNow);

                if (!_client.HasValue)
                    return new ClientInjection(0, false, "AC:Unreal is not running, so there is nothing to put the overlay into.");

                target = _client.Value.ProcessId;
                _outcomes[target] = new ClientInjection(target, false, "Putting the overlay into AC:Unreal...");
            }

            return Attempt(target);
        }

        /// <summary>Brings what is known up to date with the clients just found. Under the lock.</summary>
        private void Track(IReadOnlyList<ClientWindow> found, DateTime now)
        {
            // A client that has gone is forgotten entirely, so a process id the system hands
            // out again later counts as the new client it is.
            foreach (int gone in _firstSeen.Keys.Where(pid => found.All(c => c.ProcessId != pid)).ToList())
            {
                _firstSeen.Remove(gone);
                _outcomes.Remove(gone);
            }

            foreach (ClientWindow client in found)
            {
                if (!_firstSeen.ContainsKey(client.ProcessId))
                    _firstSeen[client.ProcessId] = now;
            }

            _clientCount = found.Count;

            // The one that has been there longest; ties go to the lower id, so the choice is stable.
            _client = found.Count == 0
                ? null
                : found.OrderBy(c => _firstSeen[c.ProcessId]).ThenBy(c => c.ProcessId).First();
        }

        private ClientInjection Attempt(int processId)
        {
            ClientInjection outcome;
            try
            {
                outcome = _inject(processId) ?? new ClientInjection(processId, false, "The injector said nothing.");
            }
            catch (Exception ex)
            {
                outcome = new ClientInjection(processId, false, "Putting the overlay into AC:Unreal failed: " + ex.Message);
            }

            lock (_gate)
            {
                if (_firstSeen.ContainsKey(processId))
                    _outcomes[processId] = outcome;
            }

            Attempted?.Invoke(this, outcome);
            return outcome;
        }

        /// <summary>
        /// The running game clients: every ACUnreal process that owns a window titled
        /// "AC:Unreal...". The launcher runs under the same name and owns none.
        /// </summary>
        public static IReadOnlyList<ClientWindow> FindClients()
        {
            List<ClientWindow> clients = new List<ClientWindow>();
            Process[] running = Process.GetProcessesByName(ProcessName);

            foreach (Process process in running)
            {
                try
                {
                    if (process.MainWindowHandle != IntPtr.Zero
                        && process.MainWindowTitle.StartsWith(WindowTitlePrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        clients.Add(new ClientWindow(process.Id, process.MainWindowTitle));
                    }
                }
                catch (Exception)
                {
                    // Closing while it was being looked at: not a client any more.
                }
                finally
                {
                    process.Dispose();
                }
            }

            return clients;
        }
    }
}
