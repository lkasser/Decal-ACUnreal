using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Decal.Adapter.Hosting;

namespace Decal.Adapter.IDQueue
{
    /// <summary>
    /// A queue shared by several callers, handing out their actions one at a time and taking
    /// turns between callers, so one that queues a hundred cannot starve one that queues one.
    /// </summary>
    /// <remarks>
    /// An action is handed out when it is valid now, dropped when it becomes permanently
    /// invalid or its time runs out, and dropped after being handed out the maximum number of
    /// times without being taken off the queue. The same action queued by two callers is one
    /// action with two callers.
    /// </remarks>
    public abstract class FairRoundRobinScheduleQueue<CALLERTYPE, ACTIONTYPE>
    {
        private readonly int _maximumTries;
        private readonly ACTIONTYPE _nullAction;
        private readonly List<CALLERTYPE> _callers = new List<CALLERTYPE>();
        private readonly Dictionary<CALLERTYPE, List<Entry>> _queued = new Dictionary<CALLERTYPE, List<Entry>>();
        private int _turn;

        protected FairRoundRobinScheduleQueue(int pMaximumTryCount, ACTIONTYPE pNullAction)
        {
            _maximumTries = Math.Max(1, pMaximumTryCount);
            _nullAction = pNullAction;
        }

        /// <summary>An action went from the queue, for whatever reason.</summary>
        public event EventHandler<ActionRemovedEventArgs> OnActionRemoved;

        public int CallerCount => _callers.Count;

        public int ActionCount => _queued.Values.SelectMany(e => e).Select(e => e.Action).Distinct().Count();

        protected abstract bool IsActionValidNow(ACTIONTYPE action);

        protected abstract bool IsActionPermanentlyInvalid(ACTIONTYPE action);

        public void Enqueue(CALLERTYPE caller, ACTIONTYPE action, DateTime expiration)
        {
            if (!_queued.TryGetValue(caller, out List<Entry> entries))
            {
                entries = new List<Entry>();
                _queued[caller] = entries;
                _callers.Add(caller);
            }

            Entry existing = entries.FirstOrDefault(e => EqualityComparer<ACTIONTYPE>.Default.Equals(e.Action, action));
            if (existing != null)
                existing.Expiration = expiration > existing.Expiration ? expiration : existing.Expiration;
            else
                entries.Add(new Entry(action, expiration));
        }

        /// <summary>The next action due, by turns between callers, and who asked for it; the null action when none is.</summary>
        public ACTIONTYPE GetNextAction(ref CALLERTYPE requester)
        {
            Purge();

            for (int i = 0; i < _callers.Count; i++)
            {
                int index = (_turn + i) % _callers.Count;
                CALLERTYPE caller = _callers[index];
                Entry due = _queued[caller].FirstOrDefault(e => IsActionValidNow(e.Action));
                if (due == null)
                    continue;

                _turn = index + 1;
                requester = caller;
                ACTIONTYPE action = due.Action;

                if (++due.Tries >= _maximumTries)
                    Remove(caller, due);

                return action;
            }

            return _nullAction;
        }

        public void DeleteAction(ACTIONTYPE action)
        {
            foreach (CALLERTYPE caller in _callers.ToList())
            {
                Entry entry = _queued[caller].FirstOrDefault(e => EqualityComparer<ACTIONTYPE>.Default.Equals(e.Action, action));
                if (entry != null)
                    Remove(caller, entry);
            }
        }

        public void DeleteCaller(CALLERTYPE caller)
        {
            if (!_queued.TryGetValue(caller, out List<Entry> entries))
                return;

            foreach (Entry entry in entries.ToList())
                Remove(caller, entry);
        }

        public void ClearAll()
        {
            foreach (CALLERTYPE caller in _callers.ToList())
                DeleteCaller(caller);
        }

        public ReadOnlyCollection<ACTIONTYPE> GetActionsForCaller(CALLERTYPE caller)
            => (_queued.TryGetValue(caller, out List<Entry> entries) ? entries.Select(e => e.Action).ToList() : new List<ACTIONTYPE>()).AsReadOnly();

        public ReadOnlyCollection<CALLERTYPE> GetCallersForAction(ACTIONTYPE action)
            => _callers.Where(c => _queued[c].Any(e => EqualityComparer<ACTIONTYPE>.Default.Equals(e.Action, action))).ToList().AsReadOnly();

        private void Purge()
        {
            DateTime now = DateTime.Now;
            foreach (CALLERTYPE caller in _callers.ToList())
            {
                foreach (Entry entry in _queued[caller].ToList())
                {
                    if (entry.Expiration < now || IsActionPermanentlyInvalid(entry.Action))
                        Remove(caller, entry);
                }
            }
        }

        private void Remove(CALLERTYPE caller, Entry entry)
        {
            List<Entry> entries = _queued[caller];
            entries.Remove(entry);

            if (entries.Count == 0)
            {
                int index = _callers.IndexOf(caller);
                _callers.RemoveAt(index);
                _queued.Remove(caller);
                if (_turn > index)
                    _turn--;
            }

            if (!_queued.Values.Any(list => list.Any(e => EqualityComparer<ACTIONTYPE>.Default.Equals(e.Action, entry.Action))))
                OnActionRemoved?.Invoke(this, new ActionRemovedEventArgs(entry.Action));
        }

        private sealed class Entry
        {
            internal Entry(ACTIONTYPE action, DateTime expiration)
            {
                Action = action;
                Expiration = expiration;
            }

            public ACTIONTYPE Action { get; }

            public DateTime Expiration { get; set; }

            public int Tries { get; set; }
        }

        public class ActionRemovedEventArgs : EventArgs
        {
            internal ActionRemovedEventArgs(ACTIONTYPE action)
            {
                Action = action;
            }

            public ACTIONTYPE Action { get; }
        }
    }

    /// <summary>
    /// Decal's shared appraisal queue: plugins ask for objects to be identified, and it asks
    /// the server for them one at a time, taking turns between plugins.
    /// </summary>
    /// <remarks>
    /// Requests go out on the host's tick, no faster than one every <see cref="Interval"/>, so
    /// a plugin that queues its whole inventory does not flood the server; an object is dropped
    /// from the queue when its appraisal arrives, when it leaves the world, or after three tries.
    /// </remarks>
    public class FairIDQueue : FairRoundRobinScheduleQueue<Assembly, int>
    {
        private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(300);
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

        private readonly DecalRuntime _runtime;
        private TimeSpan _sinceLast = Interval;

        internal FairIDQueue(DecalRuntime runtime)
            : base(3, 0)
        {
            _runtime = runtime;
        }

        public event EventHandler<UserIDRequestProcessedEventArgs> UserIDRequestProcessed;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void AddToQueue(int lObjectID) => Enqueue(Assembly.GetCallingAssembly(), lObjectID, DateTime.Now + DefaultTimeout);

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void AddToQueue(int lObjectID, DateTime pTimeout) => Enqueue(Assembly.GetCallingAssembly(), lObjectID, pTimeout);

        /// <summary>Drops an object from the queue: it has been identified some other way.</summary>
        public void ShortcircuitID(int lObjectID) => DeleteAction(lObjectID);

        protected override bool IsActionValidNow(int action) => _runtime.Host.World.TryGet(unchecked((uint)action), out _);

        protected override bool IsActionPermanentlyInvalid(int action) => !_runtime.Host.World.TryGet(unchecked((uint)action), out _);

        /// <summary>Sends the next request when one is due. Game thread, from the tick.</summary>
        internal void Pump(TimeSpan elapsed)
        {
            _sinceLast += elapsed;
            if (_sinceLast < Interval || CallerCount == 0)
                return;

            Assembly requester = null;
            int next = GetNextAction(ref requester);
            if (next == 0)
                return;

            _sinceLast = TimeSpan.Zero;
            _runtime.Core.Actions.RequestId(next);
        }

        /// <summary>An appraisal arrived: whoever was waiting for it is told, and it leaves the queue.</summary>
        internal void OnIdentified(uint id)
        {
            int objectId = unchecked((int)id);
            bool wanted = GetCallersForAction(objectId).Count > 0;
            DeleteAction(objectId);

            if (wanted)
                _runtime.Raise(UserIDRequestProcessed, this, new UserIDRequestProcessedEventArgs(objectId), nameof(UserIDRequestProcessed));
        }
    }

    public class UserIDRequestProcessedEventArgs : EventArgs
    {
        internal UserIDRequestProcessedEventArgs(int objectId)
        {
            ObjectId = objectId;
        }

        public int ObjectId { get; }
    }
}
