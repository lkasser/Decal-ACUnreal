using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AC.Host.Decoding;
using AC.Protocol;

namespace AC.Host.Tests
{
    /// <summary>
    /// Builds server messages the way ACE's writers do, so decoder tests exercise
    /// the real layouts rather than whatever the decoder happens to expect.
    /// </summary>
    /// <remarks>
    /// The stream starts with the opcode, exactly like ACE's GameMessage, so that
    /// <see cref="Align"/> pads relative to the same origin the decoder assumes.
    /// </remarks>
    internal sealed class WireWriter
    {
        private readonly MemoryStream _stream = new MemoryStream();
        private readonly BinaryWriter _writer;

        public WireWriter(uint opcode)
        {
            _writer = new BinaryWriter(_stream);
            _writer.Write(opcode);
        }

        public long Position => _stream.Position;

        public WireWriter U8(byte v) { _writer.Write(v); return this; }
        public WireWriter I8(sbyte v) { _writer.Write(v); return this; }
        public WireWriter U16(ushort v) { _writer.Write(v); return this; }
        public WireWriter U32(uint v) { _writer.Write(v); return this; }
        public WireWriter I32(int v) { _writer.Write(v); return this; }
        public WireWriter U64(ulong v) { _writer.Write(v); return this; }
        public WireWriter F32(float v) { _writer.Write(v); return this; }
        public WireWriter F64(double v) { _writer.Write(v); return this; }

        public WireWriter Bytes(byte[] v) { _writer.Write(v); return this; }

        /// <summary>ACE's WriteString16L: length, 1252 bytes, pad the field to a multiple of four.</summary>
        public WireWriter String16L(string s)
        {
            s ??= string.Empty;
            byte[] bytes = Encoding.Latin1.GetBytes(s);
            _writer.Write((ushort)bytes.Length);
            _writer.Write(bytes);
            int pad = (4 - ((2 + bytes.Length) % 4)) % 4;
            for (int i = 0; i < pad; i++) _writer.Write((byte)0);
            return this;
        }

        /// <summary>ACE's Align: pad to a multiple of four from the start of the message.</summary>
        public WireWriter Align()
        {
            while (_stream.Length % 4 != 0) _writer.Write((byte)0);
            return this;
        }

        public WireWriter PackedDword(uint value)
        {
            if (value <= 32767)
            {
                _writer.Write((ushort)value);
            }
            else
            {
                uint packed = (value << 16) | ((value >> 16) | 0x8000);
                _writer.Write(packed);
            }

            return this;
        }

        public WireWriter PackedDwordOfKnownType(uint value, uint type)
        {
            if ((value & type) > 0) value -= type;
            return PackedDword(value);
        }

        public WireWriter HashTableHeader(int count, int buckets = 16)
        {
            _writer.Write((ushort)count);
            _writer.Write((ushort)buckets);
            return this;
        }

        public WireWriter IntTable(IReadOnlyDictionary<uint, int> values)
        {
            HashTableHeader(values.Count);
            foreach (KeyValuePair<uint, int> kv in values) { U32(kv.Key); I32(kv.Value); }
            return this;
        }

        public WireWriter FloatTable(IReadOnlyDictionary<uint, double> values)
        {
            HashTableHeader(values.Count);
            foreach (KeyValuePair<uint, double> kv in values) { U32(kv.Key); F64(kv.Value); }
            return this;
        }

        public WireWriter StringTable(IReadOnlyDictionary<uint, string> values)
        {
            HashTableHeader(values.Count);
            foreach (KeyValuePair<uint, string> kv in values) { U32(kv.Key); String16L(kv.Value); }
            return this;
        }

        public WireWriter BoolTable(IReadOnlyDictionary<uint, bool> values)
        {
            HashTableHeader(values.Count);
            foreach (KeyValuePair<uint, bool> kv in values) { U32(kv.Key); U32(kv.Value ? 1u : 0u); }
            return this;
        }

        /// <summary>Position.Serialize, the 32-byte form with landblock and quaternion.</summary>
        public WireWriter Location(uint cell, float x, float y, float z, float qw = 1, float qx = 0, float qy = 0, float qz = 0)
        {
            U32(cell); F32(x); F32(y); F32(z); F32(qw); F32(qx); F32(qy); F32(qz);
            return this;
        }

        /// <summary>
        /// A motion message: guid, instance sequence, then the movement block.
        /// Mirrors GameMessageUpdateMotion followed by MovementData with a header.
        /// </summary>
        public static WireWriter Motion(
            uint id,
            byte movementType,
            ushort stance,
            bool autonomous = false,
            byte motionFlags = 0)
        {
            return new WireWriter(Opcodes.Motion)
                .U32(id)
                .U16(1)                       // instance sequence
                .U16(2)                       // movement sequence
                .U16(3)                       // server control sequence
                .U8((byte)(autonomous ? 1 : 0))
                .Align()
                .U8(movementType)
                .U8(motionFlags)
                .U16(stance);
        }

        /// <summary>
        /// The interpreted-state body: flags and a queued-animation count share one
        /// word, then a field per set flag, then alignment.
        /// </summary>
        public WireWriter InterpretedState(
            ushort? style = null,
            ushort? forward = null,
            ushort? sidestep = null,
            ushort? turn = null,
            float? forwardSpeed = null,
            float? sidestepSpeed = null,
            float? turnSpeed = null,
            int queuedAnimations = 0,
            ushort queuedCommand = 0x3D)
        {
            uint flags = 0;
            if (style.HasValue) flags |= 0x01;
            if (forward.HasValue) flags |= 0x02;
            if (forwardSpeed.HasValue) flags |= 0x04;
            if (sidestep.HasValue) flags |= 0x08;
            if (sidestepSpeed.HasValue) flags |= 0x10;
            if (turn.HasValue) flags |= 0x20;
            if (turnSpeed.HasValue) flags |= 0x40;

            U32(flags | ((uint)queuedAnimations << 7));

            if (style.HasValue) U16(style.Value);
            if (forward.HasValue) U16(forward.Value);
            if (sidestep.HasValue) U16(sidestep.Value);
            if (turn.HasValue) U16(turn.Value);
            if (forwardSpeed.HasValue) F32(forwardSpeed.Value);
            if (sidestepSpeed.HasValue) F32(sidestepSpeed.Value);
            if (turnSpeed.HasValue) F32(turnSpeed.Value);

            for (int i = 0; i < queuedAnimations; i++)
            {
                U16(queuedCommand);   // command
                U16(0);      // packed sequence
                F32(1f);     // speed
            }

            return Align();
        }

        /// <summary>An origin: cell id and position, with no rotation.</summary>
        public WireWriter Origin(uint cell = 0xA9B40019, float x = 1f, float y = 2f, float z = 3f)
            => U32(cell).F32(x).F32(y).F32(z);

        /// <summary>
        /// Move-to parameters: flags, distance to object, minimum and fail distance,
        /// speed, walk/run threshold, desired heading. No trailing padding.
        /// </summary>
        public WireWriter MoveToParameters(float desiredHeading)
        {
            U32(0);
            F32(0.6f).F32(0f).F32(float.MaxValue).F32(1f).F32(15f);
            return F32(desiredHeading);
        }

        /// <summary>Turn-to parameters: flags, speed, heading. No trailing padding.</summary>
        public WireWriter TurnToParameters(float desiredHeading)
        {
            U32(0);
            F32(1f);
            return F32(desiredHeading);
        }

        public byte[] ToArray() => _stream.ToArray();

        /// <summary>The message as the assembler would hand it to a decoder.</summary>
        public AcMessage ToMessage()
        {
            byte[] bytes = ToArray();
            IReadOnlyList<AcFragment> fragments = PacketWriter.Fragment(
                BitConverter.ToUInt32(bytes, 0), bytes.AsSpan(4), 1);

            MessageAssembler assembler = new MessageAssembler();
            AcMessage message = null;
            foreach (AcFragment fragment in fragments)
            {
                if (assembler.TryAccept(fragment, out AcMessage completed))
                    message = completed;
            }

            return message ?? throw new InvalidOperationException("Message did not reassemble.");
        }

        // ---- higher-level builders mirroring ACE's writers -------------------------

        /// <summary>GameEvent envelope: player guid, sequence, event type.</summary>
        public static WireWriter GameEvent(uint playerId, uint eventType, uint sequence = 1)
            => new WireWriter(Opcodes.GameEvent).U32(playerId).U32(sequence).U32(eventType);

        /// <summary>
        /// An ObjectCreate for an ordinary item: minimal model data, physics with a
        /// position, and the weenie fields given.
        /// </summary>
        public static WireWriter ObjectCreate(
            uint id,
            string name,
            uint wcid,
            uint itemType,
            uint descriptionFlags,
            uint iconId = 0x06001234,
            uint? containerId = null,
            int? value = null,
            ushort? burden = null,
            ushort? stackSize = null,
            uint? materialType = null,
            float? workmanship = null,
            byte? itemCapacity = null,
            uint? wielderId = null,
            uint? currentlyWielded = null,
            IReadOnlyList<(uint palette, byte offset, byte length)> palettes = null,
            bool withLocation = true)
        {
            WireWriter w = new WireWriter(Opcodes.ObjectCreate).U32(id);

            // Model data
            palettes ??= Array.Empty<(uint, byte, byte)>();
            w.U8(0x11).U8((byte)palettes.Count).U8(0).U8(0);
            if (palettes.Count > 0)
                w.PackedDwordOfKnownType(0x0400007E, 0x04000000);
            foreach ((uint palette, byte offset, byte length) in palettes)
                w.PackedDwordOfKnownType(palette, 0x04000000).U8(offset).U8(length);
            w.Align();

            // Physics data: CSetup | MTable | Position, and an AnimationFrame placement.
            uint physicsFlags = 0x000001 | 0x000002 | 0x020000 | (withLocation ? 0x008000u : 0u);
            w.U32(physicsFlags).U32(0x00000004 /* state */);
            w.U32(0x65 /* placement */);
            if (withLocation)
                w.Location(0xA9B40019, 84.0f, 7.4f, 94.0f);
            w.U32(0x09000001 /* motion table */);
            w.U32(0x02000001 /* setup */);
            for (int i = 0; i < 9; i++) w.U16(0);
            w.Align();

            // Weenie data
            uint flags = 0;
            if (value.HasValue) flags |= 0x00000008;
            if (burden.HasValue) flags |= 0x00200000;
            if (stackSize.HasValue) flags |= 0x00001000;
            if (containerId.HasValue) flags |= 0x00004000;
            if (materialType.HasValue) flags |= 0x80000000;
            if (workmanship.HasValue) flags |= 0x01000000;
            if (itemCapacity.HasValue) flags |= 0x00000002;
            if (wielderId.HasValue) flags |= 0x00008000;
            if (currentlyWielded.HasValue) flags |= 0x00020000;

            w.U32(flags)
             .String16L(name)
             .PackedDword(wcid)
             .PackedDwordOfKnownType(iconId, 0x06000000)
             .U32(itemType)
             .U32(descriptionFlags)
             .Align();

            if (itemCapacity.HasValue) w.U8(itemCapacity.Value);
            if (value.HasValue) w.I32(value.Value);
            if (stackSize.HasValue) w.U16(stackSize.Value);
            if (containerId.HasValue) w.U32(containerId.Value);
            if (wielderId.HasValue) w.U32(wielderId.Value);
            if (currentlyWielded.HasValue) w.U32(currentlyWielded.Value);
            if (workmanship.HasValue) w.F32(workmanship.Value);
            if (burden.HasValue) w.U16(burden.Value);
            if (materialType.HasValue) w.U32(materialType.Value);

            return w.Align();
        }
    }
}
