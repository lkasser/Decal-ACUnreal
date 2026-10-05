using System;

namespace AC.Host.World
{
    /// <summary>
    /// Replaying a session handed over by the host before: the world's clock reads when each
    /// message was first heard, not when it is replayed.
    /// </summary>
    public sealed partial class WorldState
    {
        /// <summary>The time by the world's clock: what it stamps objects with, and what the host keeps messages by.</summary>
        internal DateTimeOffset Now => _clock();

        /// <summary>
        /// Runs <paramref name="replay"/> with the world's clock reading <paramref name="clock"/> -
        /// the time the message being replayed was first heard - so that whatever the world stamps
        /// with the time is stamped as it was then: when an object was last seen, when it went out
        /// of view and so when it is to be let go of, when a fellow was last described.
        /// </summary>
        internal void ReplayWith(Func<DateTimeOffset> clock, Action replay)
        {
            if (clock == null) throw new ArgumentNullException(nameof(clock));
            if (replay == null) throw new ArgumentNullException(nameof(replay));

            Func<DateTimeOffset> own = _clock;
            _clock = clock;
            try
            {
                replay();
            }
            finally
            {
                _clock = own;
            }
        }
    }
}
