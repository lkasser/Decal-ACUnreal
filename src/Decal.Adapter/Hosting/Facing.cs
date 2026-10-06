using System;
using AC.Host.Plugins;
using AC.Host.World;

namespace Decal.Adapter.Hosting
{
    /// <summary>
    /// Decal's FaceHeading: the character turned where it stands to face a heading, by the game's
    /// own turn keys.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Decal asked the client to turn itself, and the client did, to the degree. The host cannot
    /// ask the client anything; it can hold the client's keys through the overlay
    /// (<see cref="IGameInput"/>) and read where the client says the character faces. So a turn
    /// is a pulse: the turn key toward the heading held for as long as the angle needs at the
    /// rate the character turns - 180 degrees a second to begin with, the client's in the
    /// captures, then whatever the pulses measure - and let go. Letting go of a key makes the
    /// client say where the character is and which way it faces; that report decides whether it
    /// is done, within <see cref="Within"/> degrees, or turns again by what is left.
    /// </para>
    /// <para>
    /// A pulse is let go between ticks when the host gives a timer (<see cref="DecalRuntime.Later"/>),
    /// and otherwise on the tick it ends in, which costs a tick's turn - eighteen degrees - of
    /// precision and is made up by the next pulse. A pulse too short for the game to see is held
    /// longer the next time. A turn that has not faced its heading in <see cref="GiveUpAfter"/> is
    /// given up and the keys let go; one asked for while the game's keys cannot be pressed is not
    /// begun, and that is said once. A new FaceHeading takes the place of one under way.
    /// </para>
    /// </remarks>
    internal sealed class Facing
    {
        /// <summary>
        /// Near enough, in degrees. A turn key held for less than a frame or two is not seen by the
        /// game, and the character turns three degrees a frame, so nearer cannot be promised.
        /// </summary>
        public const double Within = 5.0;

        /// <summary>The client's turn in the captures, standing, in degrees a second.</summary>
        public const double DefaultTurnRate = 180.0;

        /// <summary>How long a turn may take to face its heading before it is given up.</summary>
        public static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(10);

        /// <summary>How long after a pulse to wait for the client's word of where it ended.</summary>
        private static readonly TimeSpan SettleFor = TimeSpan.FromMilliseconds(600);

        /// <summary>The longest a pulse is made just to be seen: a frame or two of the game's, and then some.</summary>
        private static readonly TimeSpan LongestShort = TimeSpan.FromMilliseconds(100);

        private readonly DecalRuntime _runtime;
        private Stage _stage;
        private double _goal;
        private TimeSpan _clock;
        private TimeSpan _startedAt;
        private TimeSpan _pressedAt;
        private TimeSpan _releaseAt;
        private TimeSpan _releasedAt;
        private TimeSpan _heldFor;
        private TimeSpan _shortest;
        private GameKey _key;
        private int _pulse;
        private Location? _atRelease;
        private double _headingAtPress;

        private enum Stage
        {
            Idle,
            Turning,
            Settling,
        }

        public Facing(DecalRuntime runtime)
        {
            _runtime = runtime;
        }

        /// <summary>Degrees a second the character turns, as last measured.</summary>
        public double TurnRate { get; private set; } = DefaultTurnRate;

        /// <summary>True from a FaceHeading until it faces its heading or gives up.</summary>
        public bool Busy => _stage != Stage.Idle;

        /// <summary>Turns toward a heading in degrees clockwise from north. False when it cannot begin.</summary>
        public bool Face(double heading)
        {
            IHost host = _runtime.Host;
            if (host.Character.Location == null)
            {
                _runtime.SayOnce("face before a position", "asked to face a heading before the client had said where the character is; it was not turned.");
                return false;
            }

            if (host.Input == null || !host.Input.IsAvailable)
            {
                _runtime.SayOnce("face without keys", "asked to face a heading while the game's keys cannot be pressed - the overlay is not attached, or plugins may not act; it was not turned.");
                return false;
            }

            Stop();
            _goal = Normalise(heading);
            _startedAt = _clock;
            _shortest = TimeSpan.Zero;
            Decide();
            return true;
        }

        /// <summary>On the tick. Game thread only.</summary>
        public void Tick(TimeSpan elapsed)
        {
            _clock += elapsed;
            switch (_stage)
            {
                case Stage.Turning when _clock >= _releaseAt:
                    // No timer let it go between ticks: it was held to this tick.
                    _heldFor = _clock - _pressedAt;
                    LetGo(_pulse);
                    break;

                case Stage.Settling:
                    Settle();
                    break;
            }

            if (_stage != Stage.Idle && (_clock - _startedAt > GiveUpAfter || _runtime.Host.Input?.IsAvailable != true))
            {
                if (_clock - _startedAt > GiveUpAfter)
                    _runtime.Log.Info($"[Decal] A turn to face {_goal:0} degrees gave up after {GiveUpAfter.TotalSeconds:0} s, facing {Heading():0}.");
                Stop();
            }
        }

        /// <summary>Lets go of the turn key and ends the turn; nothing when not turning.</summary>
        public void Stop()
        {
            if (_stage == Stage.Turning)
                _runtime.Host.Input?.Hold(_key, false);
            _stage = Stage.Idle;
        }

        /// <summary>
        /// After a pulse: once the client has said where it ended - a report since the key came up
        /// that is not where the pulse began - or a while has gone by, learn from it and decide again.
        /// </summary>
        private void Settle()
        {
            Location? now = _runtime.Host.Character.Location;
            double turned = Math.Abs(Difference(_headingAtPress, Heading()));
            bool reported = !Equals(now, _atRelease) && turned >= 0.5;
            if (!reported && _clock - _releasedAt < SettleFor)
                return;

            // A pulse that turned nothing was too short for the game to see - or the game is not
            // taking keys, which no length of pulse mends - so a short one is held longer next time.
            if (turned < 0.5 && _heldFor < LongestShort)
                _shortest = TimeSpan.FromTicks(Math.Min(Math.Max(_heldFor.Ticks * 2, TimeSpan.FromMilliseconds(30).Ticks), LongestShort.Ticks));
            else if (turned >= 0.5 && _heldFor > TimeSpan.FromMilliseconds(40))
                Learn(turned, _heldFor);

            Decide();
        }

        /// <summary>Done, if near enough; otherwise a pulse for what is left, at the rate the character turns.</summary>
        private void Decide()
        {
            double off = Difference(Heading(), _goal);
            if (Math.Abs(off) <= Within)
            {
                _stage = Stage.Idle;
                return;
            }

            _key = off > 0 ? GameKey.TurnRight : GameKey.TurnLeft;
            _heldFor = TimeSpan.FromSeconds(Math.Abs(off) / TurnRate);
            if (_heldFor < _shortest)
                _heldFor = _shortest;

            _headingAtPress = Heading();
            if (!_runtime.Host.Input.Hold(_key, true))
            {
                _stage = Stage.Idle;
                return;
            }

            _stage = Stage.Turning;
            _pressedAt = _clock;
            _releaseAt = _clock + _heldFor;
            int pulse = ++_pulse;
            _runtime.Later?.Invoke(_heldFor, () => LetGo(pulse));
        }

        /// <summary>The end of a pulse, by the timer or the tick, whichever comes first.</summary>
        private void LetGo(int pulse)
        {
            if (pulse != _pulse || _stage != Stage.Turning)
                return;

            _runtime.Host.Input?.Hold(_key, false);
            _stage = Stage.Settling;
            _releasedAt = _clock;
            _atRelease = _runtime.Host.Character.Location;
        }

        /// <summary>The rate a pulse measured, folded into what is known.</summary>
        private void Learn(double turned, TimeSpan held)
        {
            double measured = turned / held.TotalSeconds;
            if (measured is > 30 and < 720)
                TurnRate = (TurnRate + measured) / 2;
        }

        private double Heading()
            => _runtime.Host.Character.Location is Location l ? Wrappers.WorldObject.HeadingOf(l) : 0;

        /// <summary>The turn from one heading to another, in degrees, -180 to 180: positive to the right.</summary>
        internal static double Difference(double from, double to)
        {
            double d = Normalise(to - from);
            return d > 180 ? d - 360 : d;
        }

        private static double Normalise(double degrees)
        {
            double d = degrees % 360;
            return d < 0 ? d + 360 : d;
        }
    }
}
