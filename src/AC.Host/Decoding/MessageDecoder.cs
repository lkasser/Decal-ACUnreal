using System;
using System.Collections.Generic;
using AC.Host.World;
using AC.Protocol;

namespace AC.Host.Decoding
{
    public enum DecodeOutcome
    {
        /// <summary>The message was understood and the world updated.</summary>
        Applied,

        /// <summary>No decoder for this opcode, or nothing to do with it.</summary>
        Ignored,

        /// <summary>A decoder exists but the bytes did not fit its layout. Nothing was changed.</summary>
        Malformed,
    }

    /// <summary>
    /// Turns game messages into world-state changes.
    /// </summary>
    /// <remarks>
    /// Every layout here is transcribed from ACE's message writers, which are the
    /// reference for the servers this talks to. Field order and width are what they
    /// are because that is what the writer emits, not because they are sensible: in
    /// particular the public and private string-property updates put the object id
    /// on opposite sides of the property id, and the weenie block writes its optional
    /// fields in source order rather than flag order.
    ///
    /// Alignment is relative to the start of the message including its opcode. The
    /// payload begins four bytes in, so aligning the payload reader to four is the
    /// same thing - which is why the whole payload is parsed with one reader and
    /// never a sub-slice.
    ///
    /// Only server-to-client messages change the world. A decoder that fails partway
    /// reports <see cref="DecodeOutcome.Malformed"/> and leaves whatever it had
    /// already written; callers treat that as a decoder bug to fix from a capture,
    /// not as something to recover from.
    /// </remarks>
    public static class MessageDecoder
    {
        private const uint DataFileIconType = 0x06000000;
        private const uint DataFilePaletteType = 0x04000000;
        private const uint DataFileTextureType = 0x05000000;
        private const uint DataFileAnimationType = 0x01000000;

        /// <summary>
        /// The action type inside a client GameAction message, which is laid out as
        /// opcode, an ordering sequence, then the type. False if the message is too
        /// short to hold one.
        /// </summary>
        public static bool TryReadActionType(AcMessage message, out uint actionType)
        {
            actionType = 0;

            if (message == null || message.Opcode != Opcodes.GameAction)
                return false;

            SpanReader reader = new SpanReader(message.Payload.Span);
            return reader.TrySkip(4) && reader.TryReadUInt32(out actionType);
        }

        /// <summary>
        /// What the client sent. Movement is read because the server does not echo it back
        /// to the mover, so the only account of where the character is heading is the
        /// client's own. Selection is read for the same reason: the server is never told
        /// what the player has selected, only asked about it - a creature's health when one
        /// is selected, an object's description when one is examined - and those questions
        /// are the only sign of it there is. The rest the client does is its own business,
        /// and the server's reply tells us the outcome anyway.
        /// </summary>
        private static DecodeOutcome ApplyClientAction(AcMessage message, WorldState world)
        {
            if (message.Opcode != Opcodes.GameAction)
                return DecodeOutcome.Ignored;

            SpanReader reader = new SpanReader(message.Payload.Span);

            // The ordering sequence, then the action type.
            if (!reader.TryReadUInt32(out uint sequence) || !reader.TryReadUInt32(out uint actionType))
                return DecodeOutcome.Malformed;

            // Worth recording whether or not this action has a decoder: it is what keeps
            // an injected action from reusing a number the server has already seen.
            world.NoteClientActionSequence(sequence);

            switch (actionType)
            {
                case GameActions.AutonomousPosition:
                {
                    if (!ClientMovementReader.TryReadPositionReport(ref reader, out Location location, out MovementSequences sequences))
                        return DecodeOutcome.Malformed;

                    world.SetClientPosition(location, sequences);
                    return DecodeOutcome.Applied;
                }

                case GameActions.MoveToState:
                {
                    if (!ClientMovementReader.TryReadMotionState(ref reader, out ClientMotionState motion))
                        return DecodeOutcome.Malformed;

                    if (!ClientMovementReader.TryReadPositionReport(ref reader, out Location location, out MovementSequences sequences))
                        return DecodeOutcome.Malformed;

                    world.SetClientMotion(motion, location, sequences);
                    return DecodeOutcome.Applied;
                }

                case GameActions.IdentifyObject:
                case GameActions.QueryHealth:
                {
                    if (!reader.TryReadUInt32(out uint objectId))
                        return DecodeOutcome.Malformed;

                    world.SetClientSelection(objectId);
                    return DecodeOutcome.Applied;
                }

                // The player's own casts: the target, then the spell - as 28 captured casts
                // of Nether Arc VII and Corruption VII read - or the spell alone for one with
                // no target, as ACE's handler reads it.
                case GameActions.CastTargetedSpell:
                {
                    if (!reader.TrySkip(4) || !reader.TryReadUInt32(out uint spellId))
                        return DecodeOutcome.Malformed;

                    world.NoteClientCast(spellId);
                    return DecodeOutcome.Applied;
                }

                case GameActions.CastUntargetedSpell:
                {
                    if (!reader.TryReadUInt32(out uint spellId))
                        return DecodeOutcome.Malformed;

                    world.NoteClientCast(spellId);
                    return DecodeOutcome.Applied;
                }

                // The client has finished loading where it is: after a teleport, the end of
                // portal space. Carries nothing; captured as 0x00A1 after every PlayerTeleport.
                case GameActions.LoginComplete:
                    return Do(world.NoteLoginComplete);

                // The fellowship panel opening (1) or closing (0), as ACE's handler reads it: one
                // word. The server sends fellows' vitals only while it is open, which is why
                // Virindi Tank helped fellows only then, and why the host notes it.
                case GameActions.FellowshipUpdateRequest:
                {
                    if (!reader.TryReadUInt32(out uint open))
                        return DecodeOutcome.Malformed;

                    world.NoteFellowshipPanel(open != 0);
                    return DecodeOutcome.Applied;
                }

                default:
                    return DecodeOutcome.Ignored;
            }
        }

        /// <summary>
        /// The event type inside a GameEvent message, which is laid out as the player id,
        /// an event sequence, then the type. False if the message is too short.
        /// </summary>
        public static bool TryReadGameEventType(AcMessage message, out uint eventType)
        {
            eventType = 0;

            if (message == null || message.Opcode != Opcodes.GameEvent)
                return false;

            SpanReader reader = new SpanReader(message.Payload.Span);
            return reader.TrySkip(8) && reader.TryReadUInt32(out eventType);
        }

        public static DecodeOutcome Apply(AcMessage message, PacketDirection direction, WorldState world)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            if (world == null) throw new ArgumentNullException(nameof(world));

            if (direction != PacketDirection.Inbound)
                return ApplyClientAction(message, world);

            SpanReader reader = new SpanReader(message.Payload.Span);

            switch (message.Opcode)
            {
                case Opcodes.ObjectCreate:
                case Opcodes.UpdateObject:
                    return DecodeObjectCreate(ref reader, world);

                case Opcodes.ObjDescEvent:
                    return DecodeObjDescEvent(ref reader, world);

                case Opcodes.ObjectDelete:
                    return DecodeObjectDelete(ref reader, world);

                case Opcodes.PlayerCreate:
                    return reader.TryReadUInt32(out uint playerId)
                        ? Do(() => world.SetPlayerId(playerId))
                        : DecodeOutcome.Malformed;

                case Opcodes.ServerName:
                    return DecodeServerName(ref reader, world);

                // The character leaving the world, as ACE says it: CharacterLogOff once a logoff is
                // done, then the character list - which on its own, with a character in the world,
                // says the same - and the account booted.
                case Opcodes.CharacterLogOff:
                    return Do(() => world.LeaveWorld("the server logged the character off"));

                case Opcodes.CharacterList:
                    return Do(() => world.LeaveWorld("the server went back to the character list"));

                case Opcodes.AccountBoot:
                    return DecodeAccountBoot(ref reader, world);

                case Opcodes.InventoryRemoveObject:
                    return DecodeInventoryRemoveObject(ref reader, world);

                case Opcodes.SetStackSize:
                    return DecodeSetStackSize(ref reader, world);

                case Opcodes.PickupEvent:
                    return DecodePickupEvent(ref reader, world);

                case Opcodes.Motion:
                    return DecodeMotion(ref reader, world);

                case Opcodes.UpdatePosition:
                    return DecodeUpdatePosition(ref reader, world);

                case Opcodes.PrivateUpdatePosition:
                    return DecodePropertyPosition(ref reader, world, isPublic: false);

                case Opcodes.PublicUpdatePosition:
                    return DecodePropertyPosition(ref reader, world, isPublic: true);

                case Opcodes.VectorUpdate:
                    return DecodeVectorUpdate(ref reader, world);

                case Opcodes.SetState:
                    return DecodeSetState(ref reader, world);

                case Opcodes.ParentEvent:
                    return DecodeParentEvent(ref reader, world);

                case Opcodes.PlayEffect:
                    return DecodePlayEffect(ref reader, world);

                case Opcodes.Sound:
                    return DecodeSound(ref reader, world);

                case Opcodes.PrivateUpdatePropertyInt:
                    return DecodePropertyInt(ref reader, world, isPublic: false);

                case Opcodes.PublicUpdatePropertyInt:
                    return DecodePropertyInt(ref reader, world, isPublic: true);

                case Opcodes.PrivateUpdatePropertyInt64:
                    return DecodePropertyInt64(ref reader, world, isPublic: false);

                case Opcodes.PublicUpdatePropertyInt64:
                    return DecodePropertyInt64(ref reader, world, isPublic: true);

                case Opcodes.PrivateUpdatePropertyBool:
                    return DecodePropertyBool(ref reader, world, isPublic: false);

                case Opcodes.PublicUpdatePropertyBool:
                    return DecodePropertyBool(ref reader, world, isPublic: true);

                case Opcodes.PrivateUpdatePropertyFloat:
                    return DecodePropertyFloat(ref reader, world, isPublic: false);

                case Opcodes.PublicUpdatePropertyFloat:
                    return DecodePropertyFloat(ref reader, world, isPublic: true);

                case Opcodes.PrivateUpdatePropertyString:
                    return DecodePropertyString(ref reader, world, isPublic: false);

                case Opcodes.PublicUpdatePropertyString:
                    return DecodePropertyString(ref reader, world, isPublic: true);

                case Opcodes.PrivateUpdatePropertyDataId:
                    return DecodePropertyUInt(ref reader, world, isPublic: false, instanceId: false);

                case Opcodes.PublicUpdatePropertyDataId:
                    return DecodePropertyUInt(ref reader, world, isPublic: true, instanceId: false);

                case Opcodes.PrivateUpdatePropertyInstanceId:
                    return DecodePropertyUInt(ref reader, world, isPublic: false, instanceId: true);

                case Opcodes.PublicUpdatePropertyInstanceId:
                    return DecodePropertyUInt(ref reader, world, isPublic: true, instanceId: true);

                case Opcodes.PrivateUpdateSkill:
                    return DecodePrivateUpdateSkill(ref reader, world);

                case Opcodes.PrivateUpdateAttribute:
                    return DecodePrivateUpdateAttribute(ref reader, world);

                case Opcodes.PrivateUpdateVital:
                    return DecodePrivateUpdateVital(ref reader, world);

                case Opcodes.PublicUpdateVital:
                    return DecodePublicUpdateVital(ref reader, world);

                case Opcodes.PrivateUpdateAttribute2ndLevel:
                    return DecodeVitalCurrent(ref reader, world);

                case Opcodes.HearSpeech:
                    return DecodeHearSpeech(ref reader, world, ranged: false);

                case Opcodes.HearRangedSpeech:
                    return DecodeHearSpeech(ref reader, world, ranged: true);

                case Opcodes.ServerMessage:
                    return DecodeServerMessage(ref reader, world);

                case Opcodes.EmoteText:
                case Opcodes.SoulEmote:
                    return DecodeEmoteText(ref reader, world);

                case Opcodes.GameEvent:
                    return DecodeGameEvent(ref reader, world);

                // The teleport's number as a half-word, padded to a word: captured as 01000000.
                case Opcodes.PlayerTeleport:
                    return reader.TryReadUInt16(out ushort teleport)
                        ? Do(() => world.EnterPortalSpace(teleport))
                        : DecodeOutcome.Malformed;

                case Opcodes.TurbineChat:
                    return ChatDecoding.DecodeTurbineChat(ref reader, world);

                // "Bob was killed by a Drudge!" - to everyone near, the one who died included.
                case Opcodes.PlayerKilled:
                    return reader.TryReadString(out string killed) && reader.TrySkip(8)
                        ? Do(() => world.NotifyChat(new ChatMessage(ChatKind.Combat, killed, null, 0, 0)))
                        : DecodeOutcome.Malformed;

                default:
                    return DecodeOutcome.Ignored;
            }
        }

        private static DecodeOutcome Do(Action apply)
        {
            apply();
            return DecodeOutcome.Applied;
        }

        // ---- objects -------------------------------------------------------------

        private static DecodeOutcome DecodeObjectCreate(ref SpanReader reader, WorldState world)
        {
            if (!reader.TryReadUInt32(out uint id))
                return DecodeOutcome.Malformed;

            WorldObject obj = world.GetOrAdd(id, out bool created);
            obj.ResetCreationFields();

            if (!TryReadModelData(ref reader, obj))
                return DecodeOutcome.Malformed;

            if (!TryReadPhysicsData(ref reader, obj))
                return DecodeOutcome.Malformed;

            if (!TryReadWeenieData(ref reader, obj))
                return DecodeOutcome.Malformed;

            world.NotifyCreated(obj);
            return DecodeOutcome.Applied;
        }

        private static DecodeOutcome DecodeObjDescEvent(ref SpanReader reader, WorldState world)
        {
            if (!reader.TryReadUInt32(out uint id))
                return DecodeOutcome.Malformed;

            WorldObject obj = world.GetOrAdd(id, out _);
            obj.Palettes.Clear();

            if (!TryReadModelData(ref reader, obj))
                return DecodeOutcome.Malformed;

            if (!reader.TrySkip(4)) // instance and visual-desc sequences
                return DecodeOutcome.Malformed;

            world.NotifyUpdated(obj);
            return DecodeOutcome.Applied;
        }

        private static DecodeOutcome DecodeObjectDelete(ref SpanReader reader, WorldState world)
        {
            if (!reader.TryReadUInt32(out uint id))
                return DecodeOutcome.Malformed;

            world.Remove(id);
            return DecodeOutcome.Applied;
        }

        private static bool TryReadModelData(ref SpanReader reader, WorldObject obj)
        {
            if (!reader.TryReadByte(out byte marker) || marker != 0x11)
                return false;

            if (!reader.TryReadByte(out byte subPalettes)) return false;
            if (!reader.TryReadByte(out byte textures)) return false;
            if (!reader.TryReadByte(out byte animations)) return false;

            if (subPalettes > 0)
            {
                if (!WireReaders.TryReadPackedDwordOfKnownType(ref reader, DataFilePaletteType, out uint basePalette))
                    return false;

                obj.PaletteBaseId = basePalette;
            }

            for (int i = 0; i < subPalettes; i++)
            {
                if (!WireReaders.TryReadPackedDwordOfKnownType(ref reader, DataFilePaletteType, out uint palette)) return false;
                if (!reader.TryReadByte(out byte offset)) return false;
                if (!reader.TryReadByte(out byte length)) return false;

                obj.Palettes.Add(new PaletteSlot(palette, offset, length));
            }

            for (int i = 0; i < textures; i++)
            {
                if (!reader.TryReadByte(out _)) return false;
                if (!WireReaders.TryReadPackedDwordOfKnownType(ref reader, DataFileTextureType, out _)) return false;
                if (!WireReaders.TryReadPackedDwordOfKnownType(ref reader, DataFileTextureType, out _)) return false;
            }

            for (int i = 0; i < animations; i++)
            {
                if (!reader.TryReadByte(out _)) return false;
                if (!WireReaders.TryReadPackedDwordOfKnownType(ref reader, DataFileAnimationType, out _)) return false;
            }

            return reader.TryAlign(4);
        }

        private static bool TryReadPhysicsData(ref SpanReader reader, WorldObject obj)
        {
            if (!reader.TryReadUInt32(out uint flags)) return false;
            if (!reader.TryReadUInt32(out uint state)) return false;

            obj.PhysicsFlags = flags;
            obj.PhysicsState = state;

            if ((flags & 0x010000) != 0) // Movement
            {
                if (!reader.TryReadUInt32(out uint length)) return false;

                if (length > 0)
                {
                    if (!reader.TrySkip((int)Math.Min(length, int.MaxValue))) return false;
                    if (!reader.TrySkip(4)) return false; // autonomous flag
                }
            }
            else if ((flags & 0x020000) != 0) // AnimationFrame
            {
                if (!reader.TrySkip(4)) return false;
            }

            if ((flags & 0x008000) != 0) // Position
            {
                if (!WireReaders.TryReadLocation(ref reader, out Location location)) return false;
                obj.Location = location;
            }

            if ((flags & 0x000002) != 0) // MTable
            {
                if (!reader.TryReadUInt32(out uint motionTable)) return false;
                obj.MotionTableId = motionTable;
            }

            if ((flags & 0x000800) != 0 && !reader.TrySkip(4)) return false; // STable
            if ((flags & 0x001000) != 0 && !reader.TrySkip(4)) return false; // PeTable

            if ((flags & 0x000001) != 0) // CSetup
            {
                if (!reader.TryReadUInt32(out uint setup)) return false;
                obj.SetupId = setup;
            }

            if ((flags & 0x000020) != 0) // Parent
            {
                if (!reader.TryReadUInt32(out uint parent)) return false;
                if (!reader.TryReadUInt32(out uint location)) return false;
                obj.ParentId = parent;
                obj.ParentLocation = location;
            }

            if ((flags & 0x000040) != 0) // Children
            {
                if (!reader.TryReadInt32(out int count)) return false;
                if (count < 0 || count > reader.Remaining / 8) return false;
                if (!reader.TrySkip(count * 8)) return false;
            }

            if ((flags & 0x000080) != 0) // ObjScale
            {
                if (!reader.TryReadSingle(out float scale)) return false;
                obj.Scale = scale;
            }

            if ((flags & 0x000100) != 0 && !reader.TrySkip(4)) return false;  // Friction
            if ((flags & 0x000200) != 0 && !reader.TrySkip(4)) return false;  // Elasticity
            if ((flags & 0x040000) != 0 && !reader.TrySkip(4)) return false;  // Translucency
            if ((flags & 0x000004) != 0 && !reader.TrySkip(12)) return false; // Velocity
            if ((flags & 0x000008) != 0 && !reader.TrySkip(12)) return false; // Acceleration
            if ((flags & 0x000010) != 0 && !reader.TrySkip(12)) return false; // Omega
            if ((flags & 0x002000) != 0 && !reader.TrySkip(4)) return false;  // DefaultScript
            if ((flags & 0x004000) != 0 && !reader.TrySkip(4)) return false;  // DefaultScriptIntensity

            // Nine object sequences, two bytes each.
            if (!reader.TrySkip(18)) return false;

            return reader.TryAlign(4);
        }

        private static bool TryReadWeenieData(ref SpanReader reader, WorldObject obj)
        {
            if (!reader.TryReadUInt32(out uint flags)) return false;
            if (!reader.TryReadString(out string name)) return false;
            if (!WireReaders.TryReadPackedDword(ref reader, out uint wcid)) return false;
            if (!WireReaders.TryReadPackedDwordOfKnownType(ref reader, DataFileIconType, out uint icon)) return false;
            if (!reader.TryReadUInt32(out uint itemType)) return false;
            if (!reader.TryReadUInt32(out uint descriptionFlags)) return false;
            if (!reader.TryAlign(4)) return false;

            obj.WeenieFlags = flags;
            obj.Name = name;
            obj.WeenieClassId = wcid;
            obj.IconId = icon;
            obj.ItemType = itemType;
            obj.DescriptionFlags = descriptionFlags;

            uint flags2 = 0;
            if ((descriptionFlags & DescriptionFlags.IncludesSecondHeader) != 0)
            {
                if (!reader.TryReadUInt32(out flags2)) return false;
            }

            obj.WeenieFlags2 = flags2;

            // Source order of ACE's SerializeCreateObject, which is the wire order.
            if ((flags & 0x00000001) != 0) { if (!reader.TryReadString(out string plural)) return false; obj.PluralName = plural; }
            if ((flags & 0x00000002) != 0) { if (!reader.TryReadByte(out byte v)) return false; obj.ItemCapacity = v; }
            if ((flags & 0x00000004) != 0) { if (!reader.TryReadByte(out byte v)) return false; obj.ContainerCapacity = v; }
            if ((flags & 0x00000100) != 0) { if (!reader.TryReadUInt16(out ushort v)) return false; obj.AmmoType = v; }
            if ((flags & 0x00000008) != 0) { if (!reader.TryReadInt32(out int v)) return false; obj.Value = v; }
            if ((flags & 0x00000010) != 0) { if (!reader.TryReadUInt32(out uint v)) return false; obj.Usable = v; }
            if ((flags & 0x00000020) != 0) { if (!reader.TryReadSingle(out float v)) return false; obj.UseRadius = v; }
            if ((flags & 0x00080000) != 0) { if (!reader.TryReadUInt32(out uint v)) return false; obj.TargetType = v; }
            if ((flags & 0x00000080) != 0) { if (!reader.TryReadUInt32(out uint v)) return false; obj.UiEffects = v; }
            if ((flags & 0x00000200) != 0) { if (!reader.TryReadByte(out byte v)) return false; obj.CombatUse = (sbyte)v; }
            if ((flags & 0x00000400) != 0) { if (!reader.TryReadUInt16(out ushort v)) return false; obj.Structure = v; }
            if ((flags & 0x00000800) != 0) { if (!reader.TryReadUInt16(out ushort v)) return false; obj.MaxStructure = v; }
            if ((flags & 0x00001000) != 0) { if (!reader.TryReadUInt16(out ushort v)) return false; obj.StackSize = v; }
            if ((flags & 0x00002000) != 0) { if (!reader.TryReadUInt16(out ushort v)) return false; obj.MaxStackSize = v; }
            if ((flags & 0x00004000) != 0) { if (!reader.TryReadUInt32(out uint v)) return false; obj.ContainerId = v; }
            if ((flags & 0x00008000) != 0) { if (!reader.TryReadUInt32(out uint v)) return false; obj.WielderId = v; }
            if ((flags & 0x00010000) != 0) { if (!reader.TryReadUInt32(out uint v)) return false; obj.ValidLocations = v; }
            if ((flags & 0x00020000) != 0) { if (!reader.TryReadUInt32(out uint v)) return false; obj.CurrentlyWieldedLocation = v; }
            if ((flags & 0x00040000) != 0) { if (!reader.TryReadUInt32(out uint v)) return false; obj.Priority = v; }
            if ((flags & 0x00100000) != 0) { if (!reader.TryReadByte(out byte v)) return false; obj.RadarColor = v; }
            if ((flags & 0x00800000) != 0) { if (!reader.TryReadByte(out byte v)) return false; obj.RadarBehavior = v; }
            if ((flags & 0x08000000) != 0) { if (!reader.TryReadUInt16(out ushort v)) return false; obj.PhysicsScript = v; }
            if ((flags & 0x01000000) != 0) { if (!reader.TryReadSingle(out float v)) return false; obj.Workmanship = v; }
            if ((flags & 0x00200000) != 0) { if (!reader.TryReadUInt16(out ushort v)) return false; obj.Burden = v; }
            if ((flags & 0x00400000) != 0) { if (!reader.TryReadUInt16(out ushort v)) return false; obj.SpellId = v; }
            if ((flags & 0x02000000) != 0) { if (!reader.TryReadUInt32(out uint v)) return false; obj.HouseOwner = v; }

            if ((flags & 0x04000000) != 0) // HouseRestrictions: a RestrictionDB
            {
                if (!reader.TrySkip(12)) return false; // version, open status, monarch
                Dictionary<uint, uint> guests = new Dictionary<uint, uint>();
                if (!WireReaders.TryReadUIntTable(ref reader, guests)) return false;
            }

            if ((flags & 0x20000000) != 0) { if (!reader.TryReadUInt32(out uint v)) return false; obj.HookItemTypes = v; }
            if ((flags & 0x00000040) != 0) { if (!reader.TryReadUInt32(out uint v)) return false; obj.Monarch = v; }
            if ((flags & 0x10000000) != 0) { if (!reader.TryReadUInt16(out ushort v)) return false; obj.HookType = v; }
            if ((flags & 0x40000000) != 0) { if (!WireReaders.TryReadPackedDwordOfKnownType(ref reader, DataFileIconType, out uint v)) return false; obj.IconOverlay = v; }
            if ((flags2 & 0x01) != 0) { if (!WireReaders.TryReadPackedDwordOfKnownType(ref reader, DataFileIconType, out uint v)) return false; obj.IconUnderlay = v; }
            if ((flags & 0x80000000) != 0) { if (!reader.TryReadUInt32(out uint v)) return false; obj.MaterialType = v; }
            if ((flags2 & 0x02) != 0) { if (!reader.TryReadInt32(out int v)) return false; obj.CooldownId = v; }
            if ((flags2 & 0x04) != 0) { if (!reader.TryReadDouble(out double v)) return false; obj.CooldownDuration = v; }
            if ((flags2 & 0x08) != 0) { if (!reader.TryReadUInt32(out uint v)) return false; obj.PetOwner = v; }

            return reader.TryAlign(4);
        }

        // ---- session ---------------------------------------------------------------

        private static DecodeOutcome DecodeServerName(ref SpanReader reader, WorldState world)
        {
            if (!reader.TrySkip(8)) return DecodeOutcome.Malformed; // current and max connections
            if (!reader.TryReadString(out string name)) return DecodeOutcome.Malformed;

            world.SetServerName(name);
            return DecodeOutcome.Applied;
        }

        /// <summary>
        /// ACE's BootAccount: the reason, when it gives one, as a string - " because the character
        /// was forced to log off by an admin" - and nothing at all when it does not.
        /// </summary>
        private static DecodeOutcome DecodeAccountBoot(ref SpanReader reader, WorldState world)
        {
            string reason = string.Empty;
            if (reader.Remaining > 0 && !reader.TryReadString(out reason))
                return DecodeOutcome.Malformed;

            world.LeaveWorld(("the server booted the account " + reason.Trim()).TrimEnd());
            return DecodeOutcome.Applied;
        }

        // ---- inventory -------------------------------------------------------------

        private static DecodeOutcome DecodeInventoryRemoveObject(ref SpanReader reader, WorldState world)
        {
            if (!reader.TryReadUInt32(out uint id)) return DecodeOutcome.Malformed;

            if (world.TryGet(id, out WorldObject obj))
            {
                obj.ContainerId = null;
                obj.WielderId = null;
                world.NotifyUpdated(obj);
            }

            return DecodeOutcome.Applied;
        }

        private static DecodeOutcome DecodeSetStackSize(ref SpanReader reader, WorldState world)
        {
            if (!reader.TrySkip(1)) return DecodeOutcome.Malformed; // sequence
            if (!reader.TryReadUInt32(out uint id)) return DecodeOutcome.Malformed;
            if (!reader.TryReadUInt32(out uint stack)) return DecodeOutcome.Malformed;
            if (!reader.TryReadUInt32(out uint value)) return DecodeOutcome.Malformed;

            WorldObject obj = world.GetOrAdd(id, out _);
            obj.StackSize = (ushort)Math.Min(stack, ushort.MaxValue);
            obj.Value = (int)Math.Min(value, int.MaxValue);
            world.NotifyUpdated(obj);

            return DecodeOutcome.Applied;
        }

        /// <summary>
        /// An object has left the world's landscape: its id, then its instance and position
        /// sequences.
        /// </summary>
        /// <remarks>
        /// <para>
        /// ACE sends it to everyone who can see the object, when it is picked up off the ground,
        /// taken off, put away into a creature's corpse, or when a creature's arrow comes off the
        /// bowstring (its PickupEvent, through RemoveTrackedObject, TryDequipObjectWithNetworking,
        /// the corpse's drop and HideAmmo).
        /// </para>
        /// <para>
        /// For an item lying on the ground, held by nobody, it means someone else has picked it
        /// up: ACE takes it out of the world, forgets that this client knew it, and creates it
        /// afresh if it is ever dropped again. So it is gone, as a DeleteObject would have it.
        /// Anything held stays and only loses its place on the landscape: the character's own
        /// pickup, whose container comes first - PublicUpdateInstanceID and
        /// InventoryPutObjInContainer, then this, captured for the salvage picked up in
        /// session-20260929-1118.acap - a weapon taken off, or the arrow a skeleton nocks, which
        /// in the same capture came off the string nine times and went back each time by
        /// ParentEvent.
        /// </para>
        /// </remarks>
        private static DecodeOutcome DecodePickupEvent(ref SpanReader reader, WorldState world)
        {
            if (!reader.TryReadUInt32(out uint id) || !reader.TrySkip(4))
                return DecodeOutcome.Malformed;

            if (id == world.Character.Id || !world.TryGet(id, out WorldObject obj))
                return DecodeOutcome.Applied;

            if (!obj.ContainerId.HasValue && !obj.WielderId.HasValue)
            {
                world.Remove(id);
                return DecodeOutcome.Applied;
            }

            obj.Location = null;
            obj.ParentId = null;
            obj.ParentLocation = null;
            world.NotifyUpdated(obj);
            return DecodeOutcome.Applied;
        }

        // ---- motion ----------------------------------------------------------------

        /// <summary>Motion flag: a sticky-object guid follows the interpreted state.</summary>
        private const byte MotionFlagStickToObject = 0x01;

        /// <summary>
        /// Decodes a motion message into what the object is currently doing.
        /// </summary>
        /// <remarks>
        /// The most common message in a session by a wide margin, because every step
        /// every creature takes produces one. The body is a movement type followed by
        /// a variant chosen by it, and the interpreted-state variant is itself
        /// flag-driven, with a count of queued animations packed into the high bits
        /// of the same word that carries the flags.
        ///
        /// The trailing fields are easy to get wrong and each one matters: an origin
        /// is a cell and a position with no rotation, both move variants end with a
        /// run rate, a turn-to-object carries its heading before its parameters, and
        /// an interpreted state appends a guid when the sticky flag is set. Getting
        /// any of them wrong leaves the reader inside the next field rather than at
        /// the end of the message.
        /// </remarks>
        private static DecodeOutcome DecodeMotion(ref SpanReader reader, WorldState world)
        {
            if (!reader.TryReadUInt32(out uint id)) return DecodeOutcome.Malformed;
            if (!reader.TrySkip(2)) return DecodeOutcome.Malformed;          // instance sequence
            if (!reader.TrySkip(4)) return DecodeOutcome.Malformed;          // movement, server-control sequences
            if (!reader.TryReadByte(out byte autonomous)) return DecodeOutcome.Malformed;
            if (!reader.TryAlign(4)) return DecodeOutcome.Malformed;

            if (!reader.TryReadByte(out byte movementType)) return DecodeOutcome.Malformed;
            if (!reader.TryReadByte(out byte motionFlags)) return DecodeOutcome.Malformed;
            if (!reader.TryReadUInt16(out ushort stance)) return DecodeOutcome.Malformed;

            MovementState movement = new MovementState
            {
                Kind = (MovementKind)movementType,
                Stance = stance,
                IsAutonomous = autonomous != 0,
            };

            switch (movement.Kind)
            {
                case MovementKind.Interpreted:
                    if (!TryReadInterpretedState(ref reader, movement)) return DecodeOutcome.Malformed;

                    if ((motionFlags & MotionFlagStickToObject) != 0)
                    {
                        if (!reader.TryReadUInt32(out uint sticky)) return DecodeOutcome.Malformed;
                        movement.TargetId = sticky;
                    }

                    break;

                case MovementKind.MoveToObject:
                    if (!reader.TryReadUInt32(out uint moveTarget)) return DecodeOutcome.Malformed;
                    movement.TargetId = moveTarget;
                    if (!TryReadOrigin(ref reader)) return DecodeOutcome.Malformed;
                    if (!TryReadMoveToParameters(ref reader, movement)) return DecodeOutcome.Malformed;
                    if (!reader.TryReadSingle(out float moveToObjectRunRate)) return DecodeOutcome.Malformed;
                    movement.RunRate = moveToObjectRunRate;
                    break;

                case MovementKind.MoveToPosition:
                    if (!TryReadOrigin(ref reader)) return DecodeOutcome.Malformed;
                    if (!TryReadMoveToParameters(ref reader, movement)) return DecodeOutcome.Malformed;
                    if (!reader.TryReadSingle(out float moveToPositionRunRate)) return DecodeOutcome.Malformed;
                    movement.RunRate = moveToPositionRunRate;
                    break;

                case MovementKind.TurnToObject:
                    if (!reader.TryReadUInt32(out uint turnTarget)) return DecodeOutcome.Malformed;
                    movement.TargetId = turnTarget;
                    if (!reader.TryReadSingle(out float turnToHeading)) return DecodeOutcome.Malformed;
                    movement.DesiredHeading = turnToHeading;
                    if (!TryReadTurnToParameters(ref reader, movement)) return DecodeOutcome.Malformed;
                    break;

                case MovementKind.TurnToHeading:
                    if (!TryReadTurnToParameters(ref reader, movement)) return DecodeOutcome.Malformed;
                    break;

                default:
                    // The stop and raw-command variants carry nothing further that the
                    // world model uses; the type alone says what happened.
                    break;
            }

            WorldObject obj = world.GetOrAdd(id, out _);
            obj.Movement = movement;
            world.NotifyMoved(obj);

            return DecodeOutcome.Applied;
        }

        /// <summary>
        /// The interpreted motion state: flags in the low seven bits of a word, the
        /// number of queued animations in the rest, then one field per set flag.
        /// </summary>
        private static bool TryReadInterpretedState(ref SpanReader reader, MovementState movement)
        {
            if (!reader.TryReadUInt32(out uint packed)) return false;

            uint flags = packed & 0x7F;
            uint commandCount = packed >> 7;

            // Every command first, then every speed - the order the writer emits,
            // which is not the order the flag bits are numbered in.
            if ((flags & 0x01) != 0)
            {
                if (!reader.TryReadUInt16(out ushort style)) return false;
                movement.Stance = style;
            }

            if ((flags & 0x02) != 0)
            {
                if (!reader.TryReadUInt16(out ushort forward)) return false;
                movement.ForwardCommand = forward;
            }

            if ((flags & 0x08) != 0)
            {
                if (!reader.TryReadUInt16(out ushort sidestep)) return false;
                movement.SidestepCommand = sidestep;
            }

            if ((flags & 0x20) != 0)
            {
                if (!reader.TryReadUInt16(out ushort turn)) return false;
                movement.TurnCommand = turn;
            }

            if ((flags & 0x04) != 0)
            {
                if (!reader.TryReadSingle(out float speed)) return false;
                movement.ForwardSpeed = speed;
            }

            if ((flags & 0x10) != 0)
            {
                if (!reader.TryReadSingle(out float speed)) return false;
                movement.SidestepSpeed = speed;
            }

            if ((flags & 0x40) != 0)
            {
                if (!reader.TryReadSingle(out float speed)) return false;
                movement.TurnSpeed = speed;
            }

            // Queued animations: command, packed sequence, speed - eight bytes each.
            if (commandCount > (uint)(reader.Remaining / 8))
                return false;

            for (uint i = 0; i < commandCount; i++)
            {
                if (!reader.TrySkip(8)) return false;
            }

            return reader.TryAlign(4);
        }

        /// <summary>
        /// An origin: a cell id and a position. Sixteen bytes - unlike a full
        /// position, it carries no rotation.
        /// </summary>
        private static bool TryReadOrigin(ref SpanReader reader) => reader.TrySkip(4 + (3 * sizeof(float)));

        /// <summary>
        /// Move-to parameters: a flag word, then distance to object, minimum
        /// distance, fail distance, speed, walk/run threshold and desired heading.
        /// </summary>
        private static bool TryReadMoveToParameters(ref SpanReader reader, MovementState movement)
        {
            if (!reader.TryReadUInt32(out _)) return false;
            if (!reader.TrySkip(5 * sizeof(float))) return false;
            if (!reader.TryReadSingle(out float heading)) return false;
            movement.DesiredHeading = heading;
            return true;
        }

        /// <summary>Turn-to parameters: a flag word, a speed and a heading.</summary>
        private static bool TryReadTurnToParameters(ref SpanReader reader, MovementState movement)
        {
            if (!reader.TryReadUInt32(out _)) return false;
            if (!reader.TryReadSingle(out _)) return false;
            if (!reader.TryReadSingle(out float heading)) return false;
            movement.DesiredHeading = heading;
            return true;
        }

        // ---- position --------------------------------------------------------------

        private static DecodeOutcome DecodeUpdatePosition(ref SpanReader reader, WorldState world)
        {
            if (!reader.TryReadUInt32(out uint id)) return DecodeOutcome.Malformed;
            if (!WireReaders.TryReadPositionPack(ref reader, out Location location)) return DecodeOutcome.Malformed;

            WorldObject obj = world.GetOrAdd(id, out _);
            obj.Location = location;
            world.NotifyUpdated(obj);

            return DecodeOutcome.Applied;
        }

        private static DecodeOutcome DecodePropertyPosition(ref SpanReader reader, WorldState world, bool isPublic)
        {
            if (!reader.TrySkip(1)) return DecodeOutcome.Malformed;

            uint id;
            if (isPublic)
            {
                if (!reader.TryReadUInt32(out id)) return DecodeOutcome.Malformed;
            }
            else
            {
                id = world.Character.Id;
            }

            if (!reader.TryReadUInt32(out uint positionType)) return DecodeOutcome.Malformed;
            if (!WireReaders.TryReadLocation(ref reader, out Location location)) return DecodeOutcome.Malformed;

            // Only the current location (type 1) is the object's position; the rest
            // are recall points, which are not modelled.
            if (positionType != 1 || id == 0)
                return DecodeOutcome.Applied;

            WorldObject obj = world.GetOrAdd(id, out _);
            obj.Location = location;
            world.NotifyUpdated(obj);

            return DecodeOutcome.Applied;
        }

        // ---- physics and effects ---------------------------------------------------

        /// <summary>
        /// An object set moving: its id, a velocity, an angular velocity, then its
        /// instance and vector sequences.
        /// </summary>
        /// <remarks>
        /// Sixty-five in one session, all thirty-two bytes. Seventeen were for the
        /// player, and each arrived six messages after one of the seventeen jumps the
        /// client sent. Those carry the client's own instance sequence - 188, the number
        /// on every one of its position reports - and a vector sequence counting 1 to
        /// 17; the horizontal speed is the same 11.8 for every running jump and 0 for
        /// the one standing jump, and the vertical part lies between 4.4 and 10.2. The
        /// other forty-eight were spell projectiles, all zero, each right after the
        /// SetState that hides the projectile and the PlayEffect that explodes it: a
        /// projectile coming to rest. The angular velocity was zero in all sixty-five,
        /// so it is checked for and not kept.
        ///
        /// The 2006 community document calls the same twelve bytes an unknown zero, a
        /// heading and a height. A standing jump has zero x and y and its height in z,
        /// which is what that reading would have seen.
        /// </remarks>
        private static DecodeOutcome DecodeVectorUpdate(ref SpanReader reader, WorldState world)
        {
            if (!reader.TryReadUInt32(out uint id)) return DecodeOutcome.Malformed;
            if (!reader.TryReadSingle(out float x)) return DecodeOutcome.Malformed;
            if (!reader.TryReadSingle(out float y)) return DecodeOutcome.Malformed;
            if (!reader.TryReadSingle(out float z)) return DecodeOutcome.Malformed;
            if (!reader.TrySkip(3 * sizeof(float))) return DecodeOutcome.Malformed; // angular velocity
            if (!reader.TrySkip(4)) return DecodeOutcome.Malformed;                 // instance, vector sequences

            WorldObject obj = world.GetOrAdd(id, out _);
            obj.Velocity = new Velocity(x, y, z);
            world.NotifyMoved(obj);

            return DecodeOutcome.Applied;
        }

        /// <summary>
        /// An object's physics state has changed: its id, the state, then its instance
        /// and state sequences.
        /// </summary>
        /// <remarks>
        /// Fifty-six in one session, all twelve bytes, and every state word breaks down
        /// into ACE's PhysicsState bits with nothing left over. The two for the player
        /// carry its instance sequence of 188 and turn the hidden, non-colliding state
        /// its creation message gave it into the ordinary walking one - the end of the
        /// portal animation, which is what the community document says this message
        /// is. A portcullis whose id carries the session's own landblock (0x2B11) gains
        /// Ethereal and loses it again, twice: a door opening and closing. Forty-eight
        /// are projectiles being hidden on impact, each followed by the explosion
        /// effect and a zero VectorUpdate.
        ///
        /// The community document splits the state into two words and calls the low
        /// one a portal type. 0x0408 is the low half of the walking state.
        /// </remarks>
        private static DecodeOutcome DecodeSetState(ref SpanReader reader, WorldState world)
        {
            if (!reader.TryReadUInt32(out uint id)) return DecodeOutcome.Malformed;
            if (!reader.TryReadUInt32(out uint state)) return DecodeOutcome.Malformed;
            if (!reader.TrySkip(4)) return DecodeOutcome.Malformed; // instance, state sequences

            WorldObject obj = world.GetOrAdd(id, out _);
            obj.PhysicsState = state;
            world.NotifyUpdated(obj);

            return DecodeOutcome.Applied;
        }

        /// <summary>
        /// A child attached to a parent: the parent's id, the child's, where on the
        /// parent (ACE's ParentLocation), the placement animation, then the child's
        /// instance and position sequences.
        /// </summary>
        /// <remarks>
        /// Thirty-seven in one session, all twenty bytes, all RightHand with the
        /// RightHandCombat placement. In every one of the thirty-six whose child had
        /// already been created, the child's creation message named this parent as its
        /// wielder; in the ten where that message also carried a physics parent, the
        /// parent and the location matched. The one that came before its child's
        /// creation was a weapon a skeleton drew, and the creation eight messages later
        /// carried the same parent and location. The rest are missile creatures nocking
        /// an arrow or quarrel their creation message said they wielded but had not
        /// yet attached.
        ///
        /// The community document has the two ids and three unknown words; the last
        /// of those is the two sequences.
        /// </remarks>
        private static DecodeOutcome DecodeParentEvent(ref SpanReader reader, WorldState world)
        {
            if (!reader.TryReadUInt32(out uint parentId)) return DecodeOutcome.Malformed;
            if (!reader.TryReadUInt32(out uint childId)) return DecodeOutcome.Malformed;
            if (!reader.TryReadUInt32(out uint location)) return DecodeOutcome.Malformed;
            if (!reader.TrySkip(4)) return DecodeOutcome.Malformed; // placement
            if (!reader.TrySkip(4)) return DecodeOutcome.Malformed; // instance, position sequences

            WorldObject child = world.GetOrAdd(childId, out _);
            child.ParentId = parentId;
            child.ParentLocation = location;
            world.NotifyUpdated(child);

            return DecodeOutcome.Applied;
        }

        /// <summary>
        /// A particle script played on an object: its id, the script, and a speed.
        /// </summary>
        /// <remarks>
        /// Two hundred and seventy-seven in one session, all twelve bytes, every script
        /// a name in ACE's PlayScript enum and every speed between 0 and 1: a Nether Bolt
        /// explodes, a skeleton hit by a nether spell shows HealthDownVoid, a creature
        /// called Flare splatters. The layout is the community document's exactly.
        ///
        /// Nothing is kept. No plugin reads effects, and an event for them would belong
        /// on the host's contract rather than here, so the whole of the job is to know
        /// the bytes for what they are.
        /// </remarks>
        private static DecodeOutcome DecodePlayEffect(ref SpanReader reader, WorldState world)
        {
            if (!reader.TryReadUInt32(out _)) return DecodeOutcome.Malformed; // object id
            if (!reader.TryReadUInt32(out _)) return DecodeOutcome.Malformed; // script
            if (!reader.TryReadSingle(out _)) return DecodeOutcome.Malformed; // speed

            return DecodeOutcome.Applied;
        }

        /// <summary>
        /// A sound played at an object: its id, the sound, and a volume.
        /// </summary>
        /// <remarks>
        /// A hundred and seventy-one in one session, all twelve bytes, every sound a
        /// name in ACE's Sound enum and every volume 0.5 or 1.0. The player made
        /// PickUpItem five times and DropItem twice - the two drops the server accepted -
        /// and a Great Skeleton made BowRelease. The layout is the community document's,
        /// with its "some sort of parameter" being the volume.
        ///
        /// Kept nowhere, for the same reason as effects.
        /// </remarks>
        private static DecodeOutcome DecodeSound(ref SpanReader reader, WorldState world)
        {
            if (!reader.TryReadUInt32(out _)) return DecodeOutcome.Malformed; // object id
            if (!reader.TryReadUInt32(out _)) return DecodeOutcome.Malformed; // sound
            if (!reader.TryReadSingle(out _)) return DecodeOutcome.Malformed; // volume

            return DecodeOutcome.Applied;
        }

        // ---- property updates ------------------------------------------------------

        private static bool TryReadPropertyTarget(ref SpanReader reader, WorldState world, bool isPublic, out WorldObject obj)
        {
            obj = null;

            if (!reader.TrySkip(1)) return false; // sequence

            if (isPublic)
            {
                if (!reader.TryReadUInt32(out uint id)) return false;
                obj = world.GetOrAdd(id, out _);
                return true;
            }

            if (world.Character.Id == 0)
                return true; // valid message; nowhere to put it

            obj = world.GetOrAdd(world.Character.Id, out _);
            return true;
        }

        private static DecodeOutcome DecodePropertyInt(ref SpanReader reader, WorldState world, bool isPublic)
        {
            if (!TryReadPropertyTarget(ref reader, world, isPublic, out WorldObject obj)) return DecodeOutcome.Malformed;
            if (!reader.TryReadUInt32(out uint property)) return DecodeOutcome.Malformed;
            if (!reader.TryReadInt32(out int value)) return DecodeOutcome.Malformed;

            if (obj == null) return DecodeOutcome.Ignored;

            obj.Ints[property] = value;
            world.NotifyUpdated(obj);
            if (obj.Id == world.Character.Id) world.NotifyCharacterUpdated();

            return DecodeOutcome.Applied;
        }

        private static DecodeOutcome DecodePropertyInt64(ref SpanReader reader, WorldState world, bool isPublic)
        {
            if (!TryReadPropertyTarget(ref reader, world, isPublic, out WorldObject obj)) return DecodeOutcome.Malformed;
            if (!reader.TryReadUInt32(out uint property)) return DecodeOutcome.Malformed;
            if (!reader.TryReadUInt64(out ulong value)) return DecodeOutcome.Malformed;

            if (obj == null) return DecodeOutcome.Ignored;

            obj.Int64s[property] = (long)value;
            world.NotifyUpdated(obj);
            return DecodeOutcome.Applied;
        }

        private static DecodeOutcome DecodePropertyBool(ref SpanReader reader, WorldState world, bool isPublic)
        {
            if (!TryReadPropertyTarget(ref reader, world, isPublic, out WorldObject obj)) return DecodeOutcome.Malformed;
            if (!reader.TryReadUInt32(out uint property)) return DecodeOutcome.Malformed;
            if (!reader.TryReadUInt32(out uint value)) return DecodeOutcome.Malformed;

            if (obj == null) return DecodeOutcome.Ignored;

            obj.Bools[property] = value != 0;
            world.NotifyUpdated(obj);
            return DecodeOutcome.Applied;
        }

        private static DecodeOutcome DecodePropertyFloat(ref SpanReader reader, WorldState world, bool isPublic)
        {
            if (!TryReadPropertyTarget(ref reader, world, isPublic, out WorldObject obj)) return DecodeOutcome.Malformed;
            if (!reader.TryReadUInt32(out uint property)) return DecodeOutcome.Malformed;
            if (!reader.TryReadDouble(out double value)) return DecodeOutcome.Malformed;

            if (obj == null) return DecodeOutcome.Ignored;

            obj.Floats[property] = value;
            world.NotifyUpdated(obj);
            return DecodeOutcome.Applied;
        }

        private static DecodeOutcome DecodePropertyString(ref SpanReader reader, WorldState world, bool isPublic)
        {
            // The string variants differ from every other property update: the
            // property id comes BEFORE the object id, and the string is aligned.
            if (!reader.TrySkip(1)) return DecodeOutcome.Malformed;
            if (!reader.TryReadUInt32(out uint property)) return DecodeOutcome.Malformed;

            WorldObject obj = null;
            if (isPublic)
            {
                if (!reader.TryReadUInt32(out uint id)) return DecodeOutcome.Malformed;
                obj = world.GetOrAdd(id, out _);
            }
            else if (world.Character.Id != 0)
            {
                obj = world.GetOrAdd(world.Character.Id, out _);
            }

            if (!reader.TryAlign(4)) return DecodeOutcome.Malformed;
            if (!reader.TryReadString(out string value)) return DecodeOutcome.Malformed;

            if (obj == null) return DecodeOutcome.Ignored;

            obj.Strings[property] = value;
            if (property == 1) obj.Name = value;
            world.NotifyUpdated(obj);
            return DecodeOutcome.Applied;
        }

        private static DecodeOutcome DecodePropertyUInt(ref SpanReader reader, WorldState world, bool isPublic, bool instanceId)
        {
            if (!TryReadPropertyTarget(ref reader, world, isPublic, out WorldObject obj)) return DecodeOutcome.Malformed;
            if (!reader.TryReadUInt32(out uint property)) return DecodeOutcome.Malformed;
            if (!reader.TryReadUInt32(out uint value)) return DecodeOutcome.Malformed;

            if (obj == null) return DecodeOutcome.Ignored;

            if (instanceId)
                obj.InstanceIds[property] = value;
            else
                obj.DataIds[property] = value;

            world.NotifyUpdated(obj);
            return DecodeOutcome.Applied;
        }

        // ---- character -------------------------------------------------------------

        private static DecodeOutcome DecodePrivateUpdateSkill(ref SpanReader reader, WorldState world)
        {
            if (!reader.TrySkip(1)) return DecodeOutcome.Malformed;
            if (!reader.TryReadUInt32(out uint skillId)) return DecodeOutcome.Malformed;
            if (!reader.TryReadUInt16(out ushort ranks)) return DecodeOutcome.Malformed;
            if (!reader.TrySkip(2)) return DecodeOutcome.Malformed; // adjust PP
            if (!reader.TryReadUInt32(out uint advancement)) return DecodeOutcome.Malformed;
            if (!reader.TryReadUInt32(out uint xp)) return DecodeOutcome.Malformed;
            if (!reader.TryReadUInt32(out uint init)) return DecodeOutcome.Malformed;
            if (!reader.TrySkip(12)) return DecodeOutcome.Malformed; // resistance, last used

            SkillState skill = world.Character.GetOrAddSkill(skillId);
            skill.Ranks = ranks;
            skill.AdvancementClass = advancement;
            skill.ExperienceSpent = xp;
            skill.InitLevel = init;

            world.NotifyCharacterUpdated();
            return DecodeOutcome.Applied;
        }

        private static DecodeOutcome DecodePrivateUpdateAttribute(ref SpanReader reader, WorldState world)
        {
            if (!reader.TrySkip(1)) return DecodeOutcome.Malformed;
            if (!reader.TryReadUInt32(out uint attributeId)) return DecodeOutcome.Malformed;
            if (!reader.TryReadUInt32(out uint ranks)) return DecodeOutcome.Malformed;
            if (!reader.TryReadUInt32(out uint start)) return DecodeOutcome.Malformed;
            if (!reader.TryReadUInt32(out uint xp)) return DecodeOutcome.Malformed;

            AttributeState attribute = world.Character.GetOrAddAttribute(attributeId);
            attribute.Ranks = ranks;
            attribute.StartingValue = start;
            attribute.ExperienceSpent = xp;

            world.NotifyCharacterUpdated();
            return DecodeOutcome.Applied;
        }

        private static DecodeOutcome DecodePrivateUpdateVital(ref SpanReader reader, WorldState world)
        {
            if (!reader.TrySkip(1)) return DecodeOutcome.Malformed;
            if (!reader.TryReadUInt32(out uint vitalId)) return DecodeOutcome.Malformed;
            if (!reader.TryReadUInt32(out uint ranks)) return DecodeOutcome.Malformed;
            if (!reader.TryReadUInt32(out uint start)) return DecodeOutcome.Malformed;
            if (!reader.TryReadUInt32(out uint xp)) return DecodeOutcome.Malformed;
            if (!reader.TryReadUInt32(out uint current)) return DecodeOutcome.Malformed;

            VitalState vital = world.Character.GetOrAddVital(NormalizeVitalId(vitalId));
            vital.Ranks = ranks;
            vital.StartingValue = start;
            vital.ExperienceSpent = xp;
            vital.Current = current;

            world.NotifyCharacterUpdated();
            return DecodeOutcome.Applied;
        }

        private static DecodeOutcome DecodePublicUpdateVital(ref SpanReader reader, WorldState world)
        {
            if (!reader.TrySkip(1)) return DecodeOutcome.Malformed;
            if (!reader.TryReadUInt32(out uint id)) return DecodeOutcome.Malformed;
            if (!reader.TryReadUInt32(out uint vitalId)) return DecodeOutcome.Malformed;
            if (!reader.TryReadUInt32(out uint ranks)) return DecodeOutcome.Malformed;
            if (!reader.TryReadUInt32(out uint start)) return DecodeOutcome.Malformed;
            if (!reader.TrySkip(4)) return DecodeOutcome.Malformed; // total investment
            if (!reader.TryReadUInt32(out uint current)) return DecodeOutcome.Malformed;

            if (id != world.Character.Id || id == 0)
                return DecodeOutcome.Applied;

            VitalState vital = world.Character.GetOrAddVital(NormalizeVitalId(vitalId));
            vital.Ranks = ranks;
            vital.StartingValue = start;
            vital.Current = current;

            world.NotifyCharacterUpdated();
            return DecodeOutcome.Applied;
        }

        private static DecodeOutcome DecodeVitalCurrent(ref SpanReader reader, WorldState world)
        {
            if (!reader.TrySkip(1)) return DecodeOutcome.Malformed;
            if (!reader.TryReadUInt32(out uint vitalId)) return DecodeOutcome.Malformed;
            if (!reader.TryReadUInt32(out uint current)) return DecodeOutcome.Malformed;

            world.Character.GetOrAddVital(NormalizeVitalId(vitalId)).Current = current;
            world.NotifyCharacterUpdated();
            return DecodeOutcome.Applied;
        }

        /// <summary>
        /// The server numbers vitals as MaxHealth=1, Health=2, MaxStamina=3, Stamina=4,
        /// MaxMana=5, Mana=6 and uses the even ("current") ids for current-value
        /// updates. One state per vital, keyed by the odd id.
        /// </summary>
        private static uint NormalizeVitalId(uint vitalId) => vitalId % 2 == 0 ? vitalId - 1 : vitalId;

        // ---- chat ----------------------------------------------------------------

        private static DecodeOutcome DecodeHearSpeech(ref SpanReader reader, WorldState world, bool ranged)
        {
            if (!reader.TryReadString(out string text)) return DecodeOutcome.Malformed;
            if (!reader.TryReadString(out string sender)) return DecodeOutcome.Malformed;
            if (!reader.TryReadUInt32(out uint senderId)) return DecodeOutcome.Malformed;
            if (ranged && !reader.TrySkip(4)) return DecodeOutcome.Malformed;
            if (!reader.TryReadUInt32(out uint chatType)) return DecodeOutcome.Malformed;

            world.NotifyChat(new ChatMessage(ChatKind.Speech, text, sender, senderId, chatType));
            return DecodeOutcome.Applied;
        }

        private static DecodeOutcome DecodeServerMessage(ref SpanReader reader, WorldState world)
        {
            if (!reader.TryReadString(out string text)) return DecodeOutcome.Malformed;
            if (!reader.TryReadUInt32(out uint chatType)) return DecodeOutcome.Malformed;

            world.NotifyChat(new ChatMessage(ChatKind.System, text, null, 0, chatType));
            return DecodeOutcome.Applied;
        }

        private static DecodeOutcome DecodeEmoteText(ref SpanReader reader, WorldState world)
        {
            if (!reader.TryReadUInt32(out uint senderId)) return DecodeOutcome.Malformed;
            if (!reader.TryReadString(out string sender)) return DecodeOutcome.Malformed;
            if (!reader.TryReadString(out string text)) return DecodeOutcome.Malformed;

            world.NotifyChat(new ChatMessage(ChatKind.Emote, text, sender, senderId, 0x0C));
            return DecodeOutcome.Applied;
        }

        // ---- game events -----------------------------------------------------------

        private static DecodeOutcome DecodeGameEvent(ref SpanReader reader, WorldState world)
        {
            if (!reader.TryReadUInt32(out uint playerId)) return DecodeOutcome.Malformed;
            if (!reader.TrySkip(4)) return DecodeOutcome.Malformed; // event sequence
            if (!reader.TryReadUInt32(out uint eventType)) return DecodeOutcome.Malformed;

            // Every event names the player it is for, so a session joined late still
            // learns who the player is.
            if (world.Character.Id == 0 && playerId != 0)
                world.SetPlayerId(playerId);

            switch (eventType)
            {
                case GameEvents.IdentifyObjectResponse:
                    return DecodeIdentifyResponse(ref reader, world);

                case GameEvents.ViewContents:
                    return DecodeViewContents(ref reader, world);

                case GameEvents.InventoryPutObjInContainer:
                    return DecodePutObjInContainer(ref reader, world);

                case GameEvents.WieldObject:
                    return DecodeWieldObject(ref reader, world);

                case GameEvents.PlayerDescription:
                    return DecodePlayerDescription(ref reader, world);

                case GameEvents.UpdateHealth:
                    return DecodeUpdateHealth(ref reader, world);

                case GameEvents.Tell:
                    return DecodeTell(ref reader, world);

                case GameEvents.DefenderNotification:
                    return DecodeDefenderNotification(ref reader, world);

                case GameEvents.EvasionDefenderNotification:
                    return reader.TryReadString(out string evaded)
                        ? Do(() => world.NotifyAttackEvaded(evaded))
                        : DecodeOutcome.Malformed;

                // The character's own attacks. Laid out by ACE's writers, since no recorded
                // session has the character fighting with a weapon yet; see DamageDealt.
                case GameEvents.AttackDone:
                    return reader.TryReadUInt32(out uint attackError)
                        ? Do(() => world.NotifyAttackFinished(attackError))
                        : DecodeOutcome.Malformed;

                case GameEvents.AttackerNotification:
                    return DecodeAttackerNotification(ref reader, world);

                case GameEvents.EvasionAttackerNotification:
                    return reader.TryReadString(out string evader)
                        ? Do(() => world.NotifyTargetEvaded(evader))
                        : DecodeOutcome.Malformed;

                case GameEvents.CombatCommenceAttack:
                    return Do(world.NotifyAttackCommenced);

                // A death message, already written out by the server - it goes to chat
                // rather than being taken apart.
                case GameEvents.KillerNotification:
                    return reader.TryReadString(out string killed)
                        ? Do(() => world.NotifyChat(new ChatMessage(ChatKind.Combat, killed, null, 0, 0)))
                        : DecodeOutcome.Malformed;

                case GameEvents.MagicUpdateEnchantment:
                    return DecodeEnchantment(ref reader, world);

                case GameEvents.MagicRemoveEnchantment:
                    return reader.TryReadUInt32(out uint goneId)
                        ? Do(() => world.RemoveEnchantment(goneId))
                        : DecodeOutcome.Malformed;

                // A dispel names the enchantment the same way a removal does.
                case GameEvents.MagicDispelEnchantment:
                    return reader.TryReadUInt32(out uint dispelledId)
                        ? Do(() => world.RemoveEnchantment(dispelledId))
                        : DecodeOutcome.Malformed;

                case GameEvents.MagicUpdateMultipleEnchantments:
                    return DecodeEnchantments(ref reader, world);

                case GameEvents.MagicRemoveMultipleEnchantments:
                case GameEvents.MagicDispelMultipleEnchantments:
                    return DecodeRemovedEnchantments(ref reader, world);

                case GameEvents.MagicPurgeEnchantments:
                    return Do(world.PurgeEnchantments);

                // A spell learned or forgotten: the spell as a half-word, then a half-word
                // ACE always writes as zero. Captured: learning Mana Drain Other I from a
                // scroll arrived as C3040000, spell 1219, beside the words "You learn the
                // Mana Drain Other I spell."
                case GameEvents.MagicUpdateSpell:
                    return reader.TryReadUInt16(out ushort learned)
                        ? Do(() => world.LearnSpell(learned))
                        : DecodeOutcome.Malformed;

                case GameEvents.MagicRemoveSpell:
                    return reader.TryReadUInt16(out ushort forgotten)
                        ? Do(() => world.ForgetSpell(forgotten))
                        : DecodeOutcome.Malformed;

                case GameEvents.UseDone:
                    return reader.TryReadUInt32(out uint useError)
                        ? Do(() => world.NotifyUseFinished(useError))
                        : DecodeOutcome.Malformed;

                case GameEvents.CloseGroundContainer:
                    return reader.TryReadUInt32(out uint closedId)
                        ? Do(() => world.NotifyContainerClosed(closedId))
                        : DecodeOutcome.Malformed;

                case GameEvents.InventoryPutObjectIn3D:
                    return DecodeInventoryPutObjectIn3D(ref reader, world);

                // A move the server turned down: the item, then the error. ACE's writer, and the
                // refused drop captured with the two in DecodeInventoryPutObjectIn3D's remarks.
                case GameEvents.InventoryServerSaveFailed:
                    return reader.TryReadUInt32(out uint unmoved) && reader.TryReadUInt32(out uint unmovedError)
                        ? Do(() => world.NotifyMoveRefused(new MoveRefusal(unmoved, unmovedError)))
                        : DecodeOutcome.Malformed;

                case GameEvents.SetTurbineChatChannels:
                    return DecodeSetTurbineChatChannels(ref reader, world);

                case GameEvents.FellowshipFullUpdate:
                    return DecodeFellowshipFullUpdate(ref reader, world);

                case GameEvents.FellowshipUpdateFellow:
                    return TryReadFellow(ref reader, out Fellow fellow)
                        ? Do(() => world.UpdateFellow(fellow))
                        : DecodeOutcome.Malformed;

                case GameEvents.FellowshipQuit:
                case GameEvents.FellowshipDismiss:
                    return reader.TryReadUInt32(out uint departed)
                        ? Do(() => world.RemoveFellow(departed))
                        : DecodeOutcome.Malformed;

                case GameEvents.FellowshipDisband:
                    return Do(world.DisbandFellowship);

                case GameEvents.CommunicationTransientString:
                    return reader.TryReadString(out string transient)
                        ? Do(() => world.NotifyChat(new ChatMessage(ChatKind.Transient, transient, null, 0, 0)))
                        : DecodeOutcome.Malformed;

                // Only the vendor is read: the window is open, which is what anything here
                // needs. Its wares follow, each a whole object description.
                case GameEvents.ApproachVendor:
                    return reader.TryReadUInt32(out uint vendorId)
                        ? Do(() => world.OpenVendor(vendorId))
                        : DecodeOutcome.Malformed;

                // The character died: the server's own sentence for it, as ACE writes it.
                case GameEvents.VictimNotification:
                    return reader.TryReadString(out string death)
                        ? Do(() => world.NotifyDied(death))
                        : DecodeOutcome.Malformed;

                case GameEvents.ChannelBroadcast:
                    return ChatDecoding.DecodeChannelBroadcast(ref reader, world);

                // Both carry a string-table id, not text. The "WithString" variant adds
                // the template's parameter, which is why a chat-channel join shows up
                // here with the channel name as its only payload.
                case GameEvents.WeenieError:
                    return reader.TryReadUInt32(out uint error)
                        ? Do(() => world.NotifyChat(new ChatMessage(ChatKind.Coded, string.Empty, null, 0, error)))
                        : DecodeOutcome.Malformed;

                case GameEvents.WeenieErrorWithString:
                    return reader.TryReadUInt32(out uint errorWithString) && reader.TryReadString(out string errorText)
                        ? Do(() => world.NotifyChat(new ChatMessage(ChatKind.Coded, errorText, null, 0, errorWithString)))
                        : DecodeOutcome.Malformed;

                default:
                    return DecodeOutcome.Ignored;
            }
        }

        private static DecodeOutcome DecodeIdentifyResponse(ref SpanReader reader, WorldState world)
        {
            if (!reader.TryReadUInt32(out uint id)) return DecodeOutcome.Malformed;
            if (!reader.TryReadUInt32(out uint flags)) return DecodeOutcome.Malformed;
            if (!reader.TryReadUInt32(out uint success)) return DecodeOutcome.Malformed;

            WorldObject obj = world.GetOrAdd(id, out _);

            // Write order of ACE's AppraiseInfo, which is not flag-bit order.
            if ((flags & 0x0001) != 0 && !WireReaders.TryReadIntTable(ref reader, obj.Ints)) return DecodeOutcome.Malformed;
            if ((flags & 0x2000) != 0 && !WireReaders.TryReadInt64Table(ref reader, obj.Int64s)) return DecodeOutcome.Malformed;
            if ((flags & 0x0002) != 0 && !WireReaders.TryReadBoolTable(ref reader, obj.Bools)) return DecodeOutcome.Malformed;
            if ((flags & 0x0004) != 0 && !WireReaders.TryReadFloatTable(ref reader, obj.Floats)) return DecodeOutcome.Malformed;
            if ((flags & 0x0008) != 0 && !WireReaders.TryReadStringTable(ref reader, obj.Strings)) return DecodeOutcome.Malformed;
            if ((flags & 0x1000) != 0 && !WireReaders.TryReadUIntTable(ref reader, obj.DataIds)) return DecodeOutcome.Malformed;

            if ((flags & 0x0010) != 0) // SpellBook
            {
                if (!reader.TryReadInt32(out int count)) return DecodeOutcome.Malformed;
                if (count < 0 || count > reader.Remaining / 4) return DecodeOutcome.Malformed;

                obj.SpellIds.Clear();
                for (int i = 0; i < count; i++)
                {
                    if (!reader.TryReadUInt32(out uint spell)) return DecodeOutcome.Malformed;
                    obj.SpellIds.Add(spell);
                }
            }

            AppraisalProfiles profiles = obj.Appraisal;

            if ((flags & 0x0080) != 0) // ArmorProfile: eight floats
            {
                float[] protection = new float[8];
                for (int i = 0; i < 8; i++)
                {
                    if (!reader.TryReadSingle(out protection[i])) return DecodeOutcome.Malformed;
                }

                profiles.HasArmor = true;
                profiles.ArmorProtection = protection;
            }

            if ((flags & 0x0100) != 0) // CreatureProfile
            {
                if (!reader.TryReadUInt32(out uint creatureFlags)) return DecodeOutcome.Malformed;
                if (!reader.TryReadUInt32(out uint health)) return DecodeOutcome.Malformed;
                if (!reader.TryReadUInt32(out uint healthMax)) return DecodeOutcome.Malformed;
                // CreatureProfileFlags: HasBuffsDebuffs = 0x1, ShowAttributes = 0x8.
                if ((creatureFlags & 0x8) != 0 && !reader.TrySkip(40)) return DecodeOutcome.Malformed; // ten attributes
                if ((creatureFlags & 0x1) != 0 && !reader.TrySkip(4)) return DecodeOutcome.Malformed;  // highlight masks

                profiles.HasCreature = true;
                profiles.CreatureHealth = health;
                profiles.CreatureHealthMax = healthMax;
            }

            if ((flags & 0x0020) != 0) // WeaponProfile
            {
                if (!reader.TryReadUInt32(out uint damageType)) return DecodeOutcome.Malformed;
                if (!reader.TryReadUInt32(out uint weaponTime)) return DecodeOutcome.Malformed;
                if (!reader.TryReadUInt32(out uint weaponSkill)) return DecodeOutcome.Malformed;
                if (!reader.TryReadUInt32(out uint damage)) return DecodeOutcome.Malformed;
                if (!reader.TryReadDouble(out double variance)) return DecodeOutcome.Malformed;
                if (!reader.TryReadDouble(out double damageMod)) return DecodeOutcome.Malformed;
                if (!reader.TryReadDouble(out double length)) return DecodeOutcome.Malformed;
                if (!reader.TryReadDouble(out double maxVelocity)) return DecodeOutcome.Malformed;
                if (!reader.TryReadDouble(out double offense)) return DecodeOutcome.Malformed;
                if (!reader.TryReadUInt32(out uint maxVelocityEstimated)) return DecodeOutcome.Malformed;

                profiles.HasWeapon = true;
                profiles.WeaponDamageType = damageType;
                profiles.WeaponTime = weaponTime;
                profiles.WeaponSkill = weaponSkill;
                profiles.WeaponDamage = damage;
                profiles.WeaponVariance = variance;
                profiles.WeaponDamageMod = damageMod;
                profiles.WeaponLength = length;
                profiles.WeaponMaxVelocity = maxVelocity;
                profiles.WeaponOffense = offense;
                profiles.WeaponMaxVelocityEstimated = maxVelocityEstimated;
            }

            if ((flags & 0x0040) != 0 && !reader.TrySkip(12)) return DecodeOutcome.Malformed; // HookProfile
            if ((flags & 0x0200) != 0 && !reader.TrySkip(4)) return DecodeOutcome.Malformed;  // armour enchantment masks
            if ((flags & 0x0800) != 0 && !reader.TrySkip(4)) return DecodeOutcome.Malformed;  // weapon enchantment masks
            if ((flags & 0x0400) != 0 && !reader.TrySkip(4)) return DecodeOutcome.Malformed;  // resist enchantment masks

            if ((flags & 0x4000) != 0) // ArmorLevels: nine uints
            {
                uint[] levels = new uint[9];
                for (int i = 0; i < 9; i++)
                {
                    if (!reader.TryReadUInt32(out levels[i])) return DecodeOutcome.Malformed;
                }

                profiles.HasArmorLevels = true;
                profiles.ArmorLevels = levels;
            }

            if (obj.Strings.TryGetValue(1, out string name) && !string.IsNullOrEmpty(name))
                obj.Name = name;

            obj.HasAppraisalData = true;
            obj.AppraisalSucceeded = success != 0;
            world.NotifyAppraised(obj);

            return DecodeOutcome.Applied;
        }

        private static DecodeOutcome DecodeViewContents(ref SpanReader reader, WorldState world)
        {
            if (!reader.TryReadUInt32(out uint containerId)) return DecodeOutcome.Malformed;
            if (!reader.TryReadUInt32(out uint count)) return DecodeOutcome.Malformed;
            if (count > (uint)(reader.Remaining / 8)) return DecodeOutcome.Malformed;

            List<ContainedItem> items = new List<ContainedItem>((int)count);
            for (uint i = 0; i < count; i++)
            {
                if (!reader.TryReadUInt32(out uint itemId)) return DecodeOutcome.Malformed;
                if (!reader.TryReadUInt32(out uint type)) return DecodeOutcome.Malformed;
                items.Add(new ContainedItem(itemId, type));
            }

            world.NotifyContainerViewed(new ContainerContents(containerId, items));
            return DecodeOutcome.Applied;
        }

        private static DecodeOutcome DecodePutObjInContainer(ref SpanReader reader, WorldState world)
        {
            if (!reader.TryReadUInt32(out uint itemId)) return DecodeOutcome.Malformed;
            if (!reader.TryReadUInt32(out uint containerId)) return DecodeOutcome.Malformed;
            if (!reader.TrySkip(8)) return DecodeOutcome.Malformed; // placement, container type

            WorldObject obj = world.GetOrAdd(itemId, out _);
            obj.ContainerId = containerId;
            obj.WielderId = null;
            obj.CurrentlyWieldedLocation = null;
            world.NotifyUpdated(obj);

            return DecodeOutcome.Applied;
        }

        /// <summary>
        /// An item has left the inventory for the ground. Carries its id and nothing else.
        /// </summary>
        /// <remarks>
        /// Two in one session, and their ids are two of the three items the client
        /// asked to drop; the third drop came back as InventoryServerSaveFailed carrying
        /// the third id. Each was followed by a DropItem sound and a fresh creation
        /// message for the item with no container. So this is the drop equivalent of
        /// InventoryRemoveObject, and is handled the same way.
        /// </remarks>
        private static DecodeOutcome DecodeInventoryPutObjectIn3D(ref SpanReader reader, WorldState world)
        {
            if (!reader.TryReadUInt32(out uint id)) return DecodeOutcome.Malformed;

            if (world.TryGet(id, out WorldObject obj))
            {
                obj.ContainerId = null;
                obj.WielderId = null;
                world.NotifyUpdated(obj);
            }

            return DecodeOutcome.Applied;
        }

        /// <summary>
        /// The character's chat channel numbers. Read and checked; nothing here joins
        /// a channel, so nothing is kept.
        /// </summary>
        /// <remarks>
        /// Four in one session, all ten words. The community document lists five -
        /// allegiance, general, trade, looking-for-group and roleplay - and the bytes
        /// agree with it that far: the first is the allegiance's own id, which the
        /// AllegianceUpdate event in the same session also carries, and the next four
        /// are 2, 3, 4 and 5. Five more words follow that the document predates; ACE's
        /// writer takes an allegiance and a society channel and emits ten. They are
        /// required because every sample had them, and not named because only the
        /// writer knows which is which.
        /// </remarks>
        private static DecodeOutcome DecodeSetTurbineChatChannels(ref SpanReader reader, WorldState world)
        {
            // In ACE's order: allegiance, general, trade, LFG, roleplay, olthoi, society and the
            // three societies' own - kept, since speaking in a room means naming its number.
            uint[] ids = new uint[10];
            for (int i = 0; i < ids.Length; i++)
            {
                if (!reader.TryReadUInt32(out ids[i])) return DecodeOutcome.Malformed;
            }

            world.SetTurbineChannels(ids);
            return DecodeOutcome.Applied;
        }

        // ---- the fellowship --------------------------------------------------------

        /// <summary>
        /// Every member, then the fellowship's name and leader. ACE's writer
        /// (GameEventFellowshipFullUpdate): a hash-table header of two half-words - the count
        /// and the bucket count - then each member as its id and <see cref="TryReadFellow"/>'s
        /// fields, then the name, the leader, and settings, departed members and locks this has
        /// no use for.
        /// </summary>
        /// <remarks>
        /// No recorded session has the character in a fellowship, so this is ACE's layout alone.
        /// </remarks>
        private static DecodeOutcome DecodeFellowshipFullUpdate(ref SpanReader reader, WorldState world)
        {
            if (!reader.TryReadUInt16(out ushort count)) return DecodeOutcome.Malformed;
            if (!reader.TrySkip(2)) return DecodeOutcome.Malformed; // bucket count

            List<Fellow> members = new List<Fellow>(count);
            for (int i = 0; i < count; i++)
            {
                if (!TryReadFellow(ref reader, out Fellow fellow)) return DecodeOutcome.Malformed;
                members.Add(fellow);
            }

            if (!reader.TryReadString(out string name)) return DecodeOutcome.Malformed;
            if (!reader.TryReadUInt32(out uint leader)) return DecodeOutcome.Malformed;

            world.SetFellowship(name, leader, members);
            return DecodeOutcome.Applied;
        }

        /// <summary>
        /// One member: the id, two words ACE always writes as zero (the experience and
        /// luminance caches), the level, the three maxima, the three current values, the
        /// share-loot word, then the name. UpdateFellow is the same, with a word after it saying
        /// whether the update is in full, of stats or of vitals - left unread, since every field
        /// is sent whichever it is.
        /// </summary>
        private static bool TryReadFellow(ref SpanReader reader, out Fellow fellow)
        {
            fellow = null;

            if (!reader.TryReadUInt32(out uint id)) return false;
            if (!reader.TrySkip(8)) return false;
            if (!reader.TryReadUInt32(out uint level)) return false;
            if (!reader.TryReadUInt32(out uint maxHealth)) return false;
            if (!reader.TryReadUInt32(out uint maxStamina)) return false;
            if (!reader.TryReadUInt32(out uint maxMana)) return false;
            if (!reader.TryReadUInt32(out uint health)) return false;
            if (!reader.TryReadUInt32(out uint stamina)) return false;
            if (!reader.TryReadUInt32(out uint mana)) return false;
            if (!reader.TryReadUInt32(out uint shareLoot)) return false;
            if (!reader.TryReadString(out string name)) return false;

            fellow = new Fellow(id)
            {
                Name = name,
                Level = level,
                MaxHealth = maxHealth,
                MaxStamina = maxStamina,
                MaxMana = maxMana,
                CurrentHealth = health,
                CurrentStamina = stamina,
                CurrentMana = mana,
                ShareLoot = shareLoot != 0,
            };
            return true;
        }

        private static DecodeOutcome DecodeWieldObject(ref SpanReader reader, WorldState world)
        {
            if (!reader.TryReadUInt32(out uint itemId)) return DecodeOutcome.Malformed;
            if (!reader.TryReadUInt32(out uint location)) return DecodeOutcome.Malformed;

            WorldObject obj = world.GetOrAdd(itemId, out _);
            obj.WielderId = world.Character.Id == 0 ? null : world.Character.Id;
            obj.CurrentlyWieldedLocation = location;
            obj.ContainerId = null;
            world.NotifyUpdated(obj);

            return DecodeOutcome.Applied;
        }

        /// <summary>
        /// One enchantment. The layout was read off three that arrived in a live session;
        /// the caster landing on the player's own id for a self-buff and on a creature's
        /// for a debuff, and the degrade limit landing on the retail client's -666
        /// sentinel, are what say the fields are in the right places.
        /// </summary>
        /// <summary>
        /// A blow that landed. The attacker arrives as a name rather than an id, which is
        /// all the protocol offers here.
        /// </summary>
        private static DecodeOutcome DecodeDefenderNotification(ref SpanReader reader, WorldState world)
        {
            if (!reader.TryReadString(out string attacker)) return DecodeOutcome.Malformed;
            if (!reader.TryReadUInt32(out uint damageType)) return DecodeOutcome.Malformed;
            if (!reader.TryReadDouble(out double percentage)) return DecodeOutcome.Malformed;
            if (!reader.TryReadUInt32(out uint amount)) return DecodeOutcome.Malformed;
            if (!reader.TryReadUInt32(out uint location)) return DecodeOutcome.Malformed;
            if (!reader.TryReadUInt32(out uint critical)) return DecodeOutcome.Malformed;

            // Two words follow that were zero in every one of the thirteen samples, so
            // there is nothing to say about them yet.
            world.NotifyDamageTaken(new DamageTaken(attacker, damageType, percentage, amount, location, critical != 0));
            return DecodeOutcome.Applied;
        }

        /// <summary>
        /// A blow the character landed: the defender's message less its body part. The
        /// attack conditions at the end are read so that a short message is caught, and
        /// not kept, as with a blow taken.
        /// </summary>
        private static DecodeOutcome DecodeAttackerNotification(ref SpanReader reader, WorldState world)
        {
            if (!reader.TryReadString(out string defender)) return DecodeOutcome.Malformed;
            if (!reader.TryReadUInt32(out uint damageType)) return DecodeOutcome.Malformed;
            if (!reader.TryReadDouble(out double percentage)) return DecodeOutcome.Malformed;
            if (!reader.TryReadUInt32(out uint amount)) return DecodeOutcome.Malformed;
            if (!reader.TryReadUInt32(out uint critical)) return DecodeOutcome.Malformed;
            if (!reader.TryReadUInt64(out _)) return DecodeOutcome.Malformed; // attack conditions

            world.NotifyDamageDealt(new DamageDealt(defender, damageType, percentage, amount, critical != 0));
            return DecodeOutcome.Applied;
        }

        private static DecodeOutcome DecodeEnchantment(ref SpanReader reader, WorldState world)
        {
            if (!TryReadEnchantment(ref reader, out Enchantment enchantment))
                return DecodeOutcome.Malformed;

            world.SetEnchantment(enchantment);
            return DecodeOutcome.Applied;
        }

        /// <summary>
        /// One enchantment, as MagicUpdateEnchantment carries it and as a login's registry and
        /// MagicUpdateMultipleEnchantments list them: the same record in all three.
        /// </summary>
        /// <remarks>
        /// The spell, its layer, its family and a flag are half-words; the flag says whether
        /// the spell-set word is present at the end. ACE sets it always, so the family came out
        /// 0x10000 too high when the two half-words were read as one; read apart, the family
        /// matched the client's spell table for all 391 enchantments of seven captured logins.
        /// </remarks>
        private static bool TryReadEnchantment(ref SpanReader reader, out Enchantment enchantment)
        {
            enchantment = null;

            if (!reader.TryReadUInt32(out uint packedId)) return false;
            if (!reader.TryReadUInt16(out ushort category)) return false;
            if (!reader.TryReadUInt16(out ushort hasSpellSet)) return false;
            if (!reader.TryReadUInt32(out uint power)) return false;
            if (!reader.TryReadDouble(out double startTime)) return false;
            if (!reader.TryReadDouble(out double duration)) return false;
            if (!reader.TryReadUInt32(out uint caster)) return false;
            if (!reader.TryReadSingle(out float degradeModifier)) return false;
            if (!reader.TryReadSingle(out float degradeLimit)) return false;
            if (!reader.TryReadDouble(out double lastDegraded)) return false;
            if (!reader.TryReadUInt32(out uint statModType)) return false;
            if (!reader.TryReadUInt32(out uint statModKey)) return false;
            if (!reader.TryReadSingle(out float statModValue)) return false;

            uint spellSetId = 0;
            if (hasSpellSet != 0 && !reader.TryReadUInt32(out spellSetId)) return false;

            enchantment = new Enchantment(packedId)
            {
                Category = category,
                PowerLevel = power,
                StartTime = startTime,
                Duration = duration,
                CasterId = caster,
                DegradeModifier = degradeModifier,
                DegradeLimit = degradeLimit,
                LastTimeDegraded = lastDegraded,
                StatModType = statModType,
                StatModKey = statModKey,
                StatModValue = statModValue,
                SpellSetId = spellSetId,
            };

            return true;
        }

        /// <summary>A count, then that many enchantments: MagicUpdateMultipleEnchantments, as ACE writes it.</summary>
        private static bool TryReadEnchantmentList(ref SpanReader reader, List<Enchantment> into)
        {
            if (!reader.TryReadUInt32(out uint count) || count > reader.Remaining / 60)
                return false;

            for (uint i = 0; i < count; i++)
            {
                if (!TryReadEnchantment(ref reader, out Enchantment enchantment))
                    return false;

                into.Add(enchantment);
            }

            return true;
        }

        private static DecodeOutcome DecodeEnchantments(ref SpanReader reader, WorldState world)
        {
            List<Enchantment> enchantments = new List<Enchantment>();
            if (!TryReadEnchantmentList(ref reader, enchantments))
                return DecodeOutcome.Malformed;

            world.SetEnchantments(enchantments);
            return DecodeOutcome.Applied;
        }

        /// <summary>
        /// A count, then that many spell-and-layer pairs, each named the way a single removal
        /// names one: MagicRemoveMultipleEnchantments and MagicDispelMultipleEnchantments, as
        /// ACE writes both.
        /// </summary>
        private static DecodeOutcome DecodeRemovedEnchantments(ref SpanReader reader, WorldState world)
        {
            if (!reader.TryReadUInt32(out uint count) || count > reader.Remaining / 4)
                return DecodeOutcome.Malformed;

            List<uint> gone = new List<uint>();
            for (uint i = 0; i < count; i++)
            {
                if (!reader.TryReadUInt32(out uint packedId))
                    return DecodeOutcome.Malformed;

                gone.Add(packedId);
            }

            world.RemoveEnchantments(gone);
            return DecodeOutcome.Applied;
        }

        private static DecodeOutcome DecodeUpdateHealth(ref SpanReader reader, WorldState world)
        {
            if (!reader.TryReadUInt32(out uint id)) return DecodeOutcome.Malformed;
            if (!reader.TryReadSingle(out float health)) return DecodeOutcome.Malformed;

            WorldObject obj = world.GetOrAdd(id, out _);
            obj.HealthFraction = health;
            world.NotifyUpdated(obj);

            return DecodeOutcome.Applied;
        }

        private static DecodeOutcome DecodeTell(ref SpanReader reader, WorldState world)
        {
            if (!reader.TryReadString(out string text)) return DecodeOutcome.Malformed;
            if (!reader.TryReadString(out string sender)) return DecodeOutcome.Malformed;
            if (!reader.TryReadUInt32(out uint senderId)) return DecodeOutcome.Malformed;
            if (!reader.TrySkip(4)) return DecodeOutcome.Malformed; // target id
            if (!reader.TryReadUInt32(out uint chatType)) return DecodeOutcome.Malformed;

            world.NotifyChat(new ChatMessage(ChatKind.Tell, text, sender, senderId, chatType));
            return DecodeOutcome.Applied;
        }

        private static DecodeOutcome DecodePlayerDescription(ref SpanReader reader, WorldState world)
        {
            if (world.Character.Id == 0)
                return DecodeOutcome.Ignored;

            if (!reader.TryReadUInt32(out uint propertyFlags)) return DecodeOutcome.Malformed;
            if (!reader.TrySkip(4)) return DecodeOutcome.Malformed; // weenie type

            WorldObject player = world.GetOrAdd(world.Character.Id, out _);

            // Write order, not flag order.
            if ((propertyFlags & 0x0001) != 0 && !WireReaders.TryReadIntTable(ref reader, player.Ints)) return DecodeOutcome.Malformed;
            if ((propertyFlags & 0x0080) != 0 && !WireReaders.TryReadInt64Table(ref reader, player.Int64s)) return DecodeOutcome.Malformed;
            if ((propertyFlags & 0x0002) != 0 && !WireReaders.TryReadBoolTable(ref reader, player.Bools)) return DecodeOutcome.Malformed;
            if ((propertyFlags & 0x0004) != 0 && !WireReaders.TryReadFloatTable(ref reader, player.Floats)) return DecodeOutcome.Malformed;
            if ((propertyFlags & 0x0010) != 0 && !WireReaders.TryReadStringTable(ref reader, player.Strings)) return DecodeOutcome.Malformed;
            if ((propertyFlags & 0x0008) != 0 && !WireReaders.TryReadUIntTable(ref reader, player.DataIds)) return DecodeOutcome.Malformed;
            if ((propertyFlags & 0x0040) != 0 && !WireReaders.TryReadUIntTable(ref reader, player.InstanceIds)) return DecodeOutcome.Malformed;

            if ((propertyFlags & 0x0020) != 0) // positions
            {
                if (!WireReaders.TryReadHashTableHeader(ref reader, out int count)) return DecodeOutcome.Malformed;
                for (int i = 0; i < count; i++)
                {
                    if (!reader.TrySkip(4)) return DecodeOutcome.Malformed; // position type
                    if (!WireReaders.TryReadLocation(ref reader, out _)) return DecodeOutcome.Malformed;
                }
            }

            if (player.Strings.TryGetValue(1, out string name) && !string.IsNullOrEmpty(name))
                player.Name = name;

            if (!reader.TryReadUInt32(out uint vectorFlags)) return DecodeOutcome.Malformed;
            if (!reader.TrySkip(4)) return DecodeOutcome.Malformed; // has health

            if ((vectorFlags & 0x0001) != 0)
            {
                if (!reader.TryReadUInt32(out uint attributeFlags)) return DecodeOutcome.Malformed;

                for (uint attributeId = 1; attributeId <= 6; attributeId++)
                {
                    if ((attributeFlags & (1u << (int)(attributeId - 1))) == 0)
                        continue;

                    if (!reader.TryReadUInt32(out uint ranks)) return DecodeOutcome.Malformed;
                    if (!reader.TryReadUInt32(out uint start)) return DecodeOutcome.Malformed;
                    if (!reader.TryReadUInt32(out uint xp)) return DecodeOutcome.Malformed;

                    AttributeState attribute = world.Character.GetOrAddAttribute(attributeId);
                    attribute.Ranks = ranks;
                    attribute.StartingValue = start;
                    attribute.ExperienceSpent = xp;
                }

                // Health, stamina, mana: vital ids 1, 3, 5; flag bits 0x40, 0x80, 0x100.
                for (int v = 0; v < 3; v++)
                {
                    if ((attributeFlags & (0x40u << v)) == 0)
                        continue;

                    if (!reader.TryReadUInt32(out uint ranks)) return DecodeOutcome.Malformed;
                    if (!reader.TryReadUInt32(out uint start)) return DecodeOutcome.Malformed;
                    if (!reader.TryReadUInt32(out uint xp)) return DecodeOutcome.Malformed;
                    if (!reader.TryReadUInt32(out uint current)) return DecodeOutcome.Malformed;

                    VitalState vital = world.Character.GetOrAddVital((uint)(1 + v * 2));
                    vital.Ranks = ranks;
                    vital.StartingValue = start;
                    vital.ExperienceSpent = xp;
                    vital.Current = current;
                }
            }

            if ((vectorFlags & 0x0002) != 0)
            {
                if (!WireReaders.TryReadHashTableHeader(ref reader, out int count)) return DecodeOutcome.Malformed;

                for (int i = 0; i < count; i++)
                {
                    if (!reader.TryReadUInt32(out uint skillId)) return DecodeOutcome.Malformed;
                    if (!reader.TryReadUInt16(out ushort ranks)) return DecodeOutcome.Malformed;
                    if (!reader.TrySkip(2)) return DecodeOutcome.Malformed;
                    if (!reader.TryReadUInt32(out uint advancement)) return DecodeOutcome.Malformed;
                    if (!reader.TryReadUInt32(out uint xp)) return DecodeOutcome.Malformed;
                    if (!reader.TryReadUInt32(out uint init)) return DecodeOutcome.Malformed;
                    if (!reader.TrySkip(12)) return DecodeOutcome.Malformed;

                    SkillState skill = world.Character.GetOrAddSkill(skillId);
                    skill.Ranks = ranks;
                    skill.AdvancementClass = advancement;
                    skill.ExperienceSpent = xp;
                    skill.InitLevel = init;
                }
            }

            // The spellbook: a hash table of spell id to a float - 2.0 in every entry of every
            // captured login, and meaningless to anything here.
            List<uint> spellbook = null;
            if ((vectorFlags & 0x0100) != 0)
            {
                if (!WireReaders.TryReadHashTableHeader(ref reader, out int count) || count > reader.Remaining / 8)
                    return DecodeOutcome.Malformed;

                spellbook = new List<uint>(count);
                for (int i = 0; i < count; i++)
                {
                    if (!reader.TryReadUInt32(out uint spellId)) return DecodeOutcome.Malformed;
                    if (!reader.TrySkip(4)) return DecodeOutcome.Malformed;
                    spellbook.Add(spellId);
                }
            }

            // The enchantment registry: a word of flags, then a counted list for each of
            // multiplicative (1), additive (2) and cooldown (4) enchantments, and a single
            // one for vitae (8). Seven captured logins read with this, holding 44 to 87
            // enchantments between them - one with the player's own buffs among the item spells,
            // 90 minutes long and cast up to 85 minutes before - and every family in them
            // matched the client's spell table.
            List<Enchantment> enchantments = null;
            if ((vectorFlags & 0x0200) != 0)
            {
                enchantments = new List<Enchantment>();

                if (!reader.TryReadUInt32(out uint registry)) return DecodeOutcome.Malformed;

                for (uint list = 1; list <= 4; list <<= 1)
                {
                    if ((registry & list) != 0 && !TryReadEnchantmentList(ref reader, enchantments))
                        return DecodeOutcome.Malformed;
                }

                if ((registry & 8) != 0)
                {
                    if (!TryReadEnchantment(ref reader, out Enchantment vitae))
                        return DecodeOutcome.Malformed;
                    enchantments.Add(vitae);
                }
            }

            // Options, shortcuts and the inventory follow; nothing here needs them yet.
            if (spellbook != null)
                world.Character.ReplaceSpellbook(spellbook);

            if (enchantments != null)
                world.ReplaceEnchantments(enchantments);

            world.NotifyUpdated(player);
            world.NotifyCharacterUpdated();
            return DecodeOutcome.Applied;
        }
    }
}
