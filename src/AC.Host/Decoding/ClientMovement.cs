using AC.Host.World;
using AC.Protocol;

namespace AC.Host.Decoding
{
    /// <summary>Which fields a <see cref="ClientMotionState"/> carries.</summary>
    /// <remarks>
    /// Each bit means "the next field is present", in the order the fields are
    /// declared. Worked out from a live session: a standing character sends 0x0003,
    /// walking adds the three forward fields for 0x001F, and walking while turning
    /// adds the three turn fields for 0x071F.
    /// </remarks>
    public static class MotionFlags
    {
        public const uint CurrentHoldKey = 0x0001;
        public const uint CurrentStyle = 0x0002;
        public const uint ForwardCommand = 0x0004;
        public const uint ForwardHoldKey = 0x0008;
        public const uint ForwardSpeed = 0x0010;
        public const uint SidestepCommand = 0x0020;
        public const uint SidestepHoldKey = 0x0040;
        public const uint SidestepSpeed = 0x0080;
        public const uint TurnCommand = 0x0100;
        public const uint TurnHoldKey = 0x0200;
        public const uint TurnSpeed = 0x0400;
    }

    /// <summary>Motion commands seen from the client, for reading a state back.</summary>
    public static class MotionCommands
    {
        public const uint Ready = 0x8000003D;
        public const uint WalkForward = 0x45000005;
        public const uint WalkBackwards = 0x45000006;
        public const uint TurnRight = 0x6500000D;
        public const uint TurnLeft = 0x6500000E;
        public const uint SideStepRight = 0x65000013;
        public const uint SideStepLeft = 0x65000014;
    }

    /// <summary>
    /// What the client says its body is doing: AC's RawMotionState, the payload of a
    /// MoveToState action.
    /// </summary>
    /// <remarks>
    /// Absent fields are left at zero, which is also how the client reads them, so a
    /// state with no forward command simply has <see cref="ForwardCommand"/> zero.
    /// </remarks>
    public sealed class ClientMotionState
    {
        public uint Flags { get; set; }

        public uint CurrentHoldKey { get; set; }

        public uint CurrentStyle { get; set; }

        public uint ForwardCommand { get; set; }

        public uint ForwardHoldKey { get; set; }

        public float ForwardSpeed { get; set; }

        public uint SidestepCommand { get; set; }

        public uint SidestepHoldKey { get; set; }

        public float SidestepSpeed { get; set; }

        public uint TurnCommand { get; set; }

        public uint TurnHoldKey { get; set; }

        public float TurnSpeed { get; set; }

        /// <summary>True if the character is moving along the ground under its own power.</summary>
        public bool IsMoving => ForwardCommand != 0 || SidestepCommand != 0 || TurnCommand != 0;

        public override string ToString()
        {
            if (!IsMoving) return $"style=0x{CurrentStyle:X8}";

            string forward = ForwardCommand != 0 ? $" forward=0x{ForwardCommand:X8}@{ForwardSpeed:F2}" : string.Empty;
            string side = SidestepCommand != 0 ? $" side=0x{SidestepCommand:X8}@{SidestepSpeed:F2}" : string.Empty;
            string turn = TurnCommand != 0 ? $" turn=0x{TurnCommand:X8}@{TurnSpeed:F2}" : string.Empty;
            return $"style=0x{CurrentStyle:X8}{forward}{side}{turn}";
        }
    }

    /// <summary>
    /// The four sequence numbers and the contact flag that close out every movement
    /// message the client sends.
    /// </summary>
    /// <remarks>
    /// The server uses these to reject movement that was computed against a view of
    /// the world it has already replaced. They are not ours to invent: to compose a
    /// movement message we send back the ones last seen from the client.
    /// </remarks>
    public readonly struct MovementSequences
    {
        public MovementSequences(ushort instance, ushort serverControl, ushort teleport, ushort forcePosition, uint contact)
        {
            Instance = instance;
            ServerControl = serverControl;
            Teleport = teleport;
            ForcePosition = forcePosition;
            Contact = contact;
        }

        public ushort Instance { get; }

        public ushort ServerControl { get; }

        public ushort Teleport { get; }

        public ushort ForcePosition { get; }

        /// <summary>Non-zero while the character is in contact with the ground.</summary>
        public uint Contact { get; }

        public override string ToString()
            => $"instance={Instance} control={ServerControl} teleport={Teleport} force={ForcePosition} contact={Contact}";
    }

    /// <summary>Reads the movement messages the client sends.</summary>
    internal static class ClientMovementReader
    {
        public static bool TryReadMotionState(ref SpanReader reader, out ClientMotionState state)
        {
            state = null;

            if (!reader.TryReadUInt32(out uint flags)) return false;

            ClientMotionState result = new ClientMotionState { Flags = flags };

            if ((flags & MotionFlags.CurrentHoldKey) != 0)
            {
                if (!reader.TryReadUInt32(out uint v)) return false;
                result.CurrentHoldKey = v;
            }

            if ((flags & MotionFlags.CurrentStyle) != 0)
            {
                if (!reader.TryReadUInt32(out uint v)) return false;
                result.CurrentStyle = v;
            }

            if ((flags & MotionFlags.ForwardCommand) != 0)
            {
                if (!reader.TryReadUInt32(out uint v)) return false;
                result.ForwardCommand = v;
            }

            if ((flags & MotionFlags.ForwardHoldKey) != 0)
            {
                if (!reader.TryReadUInt32(out uint v)) return false;
                result.ForwardHoldKey = v;
            }

            if ((flags & MotionFlags.ForwardSpeed) != 0)
            {
                if (!reader.TryReadSingle(out float v)) return false;
                result.ForwardSpeed = v;
            }

            if ((flags & MotionFlags.SidestepCommand) != 0)
            {
                if (!reader.TryReadUInt32(out uint v)) return false;
                result.SidestepCommand = v;
            }

            if ((flags & MotionFlags.SidestepHoldKey) != 0)
            {
                if (!reader.TryReadUInt32(out uint v)) return false;
                result.SidestepHoldKey = v;
            }

            if ((flags & MotionFlags.SidestepSpeed) != 0)
            {
                if (!reader.TryReadSingle(out float v)) return false;
                result.SidestepSpeed = v;
            }

            if ((flags & MotionFlags.TurnCommand) != 0)
            {
                if (!reader.TryReadUInt32(out uint v)) return false;
                result.TurnCommand = v;
            }

            if ((flags & MotionFlags.TurnHoldKey) != 0)
            {
                if (!reader.TryReadUInt32(out uint v)) return false;
                result.TurnHoldKey = v;
            }

            if ((flags & MotionFlags.TurnSpeed) != 0)
            {
                if (!reader.TryReadSingle(out float v)) return false;
                result.TurnSpeed = v;
            }

            state = result;
            return true;
        }

        /// <summary>
        /// A landcell, an origin, a rotation, and the sequences that authorise it.
        /// </summary>
        public static bool TryReadPositionReport(ref SpanReader reader, out Location location, out MovementSequences sequences)
        {
            location = default;
            sequences = default;

            if (!reader.TryReadUInt32(out uint landblockCell)) return false;
            if (!reader.TryReadSingle(out float x)) return false;
            if (!reader.TryReadSingle(out float y)) return false;
            if (!reader.TryReadSingle(out float z)) return false;
            if (!reader.TryReadSingle(out float qw)) return false;
            if (!reader.TryReadSingle(out float qx)) return false;
            if (!reader.TryReadSingle(out float qy)) return false;
            if (!reader.TryReadSingle(out float qz)) return false;

            if (!reader.TryReadUInt16(out ushort instance)) return false;
            if (!reader.TryReadUInt16(out ushort serverControl)) return false;
            if (!reader.TryReadUInt16(out ushort teleport)) return false;
            if (!reader.TryReadUInt16(out ushort forcePosition)) return false;
            if (!reader.TryReadUInt32(out uint contact)) return false;

            location = new Location(landblockCell, x, y, z, qw, qx, qy, qz);
            sequences = new MovementSequences(instance, serverControl, teleport, forcePosition, contact);
            return true;
        }
    }
}
