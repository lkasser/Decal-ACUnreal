namespace AC.Host.World
{
    /// <summary>What kind of movement a motion message describes.</summary>
    public enum MovementKind : byte
    {
        /// <summary>Not a move to anywhere: a stance or animation change, or a stop.</summary>
        Interpreted = 0,

        RawCommand = 1,
        InterpretedCommand = 2,
        StopRawCommand = 3,
        StopInterpretedCommand = 4,
        StopCompletely = 5,
        MoveToObject = 6,
        MoveToPosition = 7,
        TurnToObject = 8,
        TurnToHeading = 9,
    }

    /// <summary>
    /// What an object is currently doing: its stance, and any walk, sidestep or turn
    /// under way.
    /// </summary>
    /// <remarks>
    /// This is the interpreted state - what the server tells every nearby client to
    /// animate - rather than the raw key presses. It is what a route follower needs
    /// in order to know whether the character is already moving, and what a combat
    /// plugin needs in order to see another creature wind up an attack.
    ///
    /// Speeds are multipliers, negative for the reverse direction: a forward speed of
    /// -0.65 is walking backwards.
    /// </remarks>
    public sealed class MovementState
    {
        public MovementKind Kind { get; internal set; }

        /// <summary>Combat stance: unarmed, sword and shield, magic, and so on.</summary>
        public ushort Stance { get; internal set; }

        /// <summary>Animation or movement command under way, 0 for none.</summary>
        public ushort ForwardCommand { get; internal set; }

        public ushort SidestepCommand { get; internal set; }

        public ushort TurnCommand { get; internal set; }

        public float ForwardSpeed { get; internal set; } = 1f;

        public float SidestepSpeed { get; internal set; } = 1f;

        public float TurnSpeed { get; internal set; } = 1f;

        /// <summary>Target of a move-to or turn-to, when the motion names one.</summary>
        public uint TargetId { get; internal set; }

        /// <summary>Heading in degrees for a turn-to-heading.</summary>
        public float DesiredHeading { get; internal set; }

        /// <summary>Run speed of the mover, on the move-to variants.</summary>
        public float RunRate { get; internal set; }

        /// <summary>True when the client asked for this rather than the server.</summary>
        public bool IsAutonomous { get; internal set; }

        /// <summary>
        /// Whether the object is travelling under its own power right now. A stance
        /// change or a gesture is movement data but not motion.
        /// </summary>
        public bool IsMoving =>
            Kind == MovementKind.MoveToObject
            || Kind == MovementKind.MoveToPosition
            || (ForwardCommand != 0 && ForwardCommand != ReadyCommand)
            || SidestepCommand != 0
            || TurnCommand != 0;

        /// <summary>The "standing still, doing nothing" command.</summary>
        public const ushort ReadyCommand = 0x3D;

        /// <summary>
        /// The command a creature is left holding when it dies: the low half of ACE's
        /// MotionCommand.Dead, 0x40000011.
        /// </summary>
        public const ushort DeadCommand = 0x11;

        /// <summary>
        /// Whether the object has died. Every kill in the recorded sessions went the same
        /// way - its health fell to nothing, the death message, this motion three or four
        /// messages later, then the object's removal as its corpse appeared - and eleven
        /// kills showed this command eleven times and on nothing that was still alive.
        /// </summary>
        public bool IsDead => ForwardCommand == DeadCommand;

        public override string ToString()
            => $"{Kind} stance={Stance} fwd={ForwardCommand}@{ForwardSpeed:F2} turn={TurnCommand}@{TurnSpeed:F2}"
               + (TargetId != 0 ? $" target=0x{TargetId:X8}" : string.Empty);
    }
}
