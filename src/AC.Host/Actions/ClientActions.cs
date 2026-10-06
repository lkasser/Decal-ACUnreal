using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AC.Host.Decoding;
using AC.Host.Plugins;
using AC.Host.World;
using AC.Host.Transport;
using AC.Protocol;

namespace AC.Host.Actions
{
    /// <summary>
    /// Builds the client-to-server messages that ask the character to do something,
    /// and hands them to the transport.
    /// </summary>
    /// <remarks>
    /// Every action travels inside one <c>GameAction</c> message: the opcode, a
    /// sequence number, the action type, then the action's own fields. ACE reads the
    /// sequence but does not validate it, so it is simply counted up - it exists so
    /// the client can correlate a reply.
    ///
    /// Field layouts are taken from ACE's handlers, which read them directly; each is
    /// noted at its method.
    /// </remarks>
    public sealed class ClientActions : IGameActions
    {
        private readonly IGameTransport _transport;
        private readonly IPluginLog _log;
        private readonly WorldState _world;
        private uint _sequence;

        public ClientActions(IGameTransport transport, IPluginLog log, WorldState world = null)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _log = log ?? throw new ArgumentNullException(nameof(log));

            // Movement needs the world: a movement message carries the position and the
            // sequences the server last authorised, and those are not ours to invent.
            _world = world;
        }

        /// <summary>
        /// Whether acting is permitted right now. The transport being able to send is not the
        /// same as the player having said it may: the host can carry actions from the start of
        /// a session and still only watch until it is told otherwise. Null means permitted.
        /// </summary>
        public Func<bool> Allowed { get; set; }

        public bool IsAvailable => _transport.CanSend && (Allowed?.Invoke() ?? true);

        /// <summary>Actions handed to the transport this session.</summary>
        public int Sent { get; private set; }

        /// <summary>Requests refused because the transport cannot send or acting is not permitted.</summary>
        public int Refused { get; private set; }

        /// <summary>
        /// Told of each appraisal asked for, before it goes: its answer is the host's alone
        /// (<see cref="AC.Host.Transport.AppraisalRequests"/>).
        /// </summary>
        public Action<uint> Appraising { get; set; }

        /// <summary>Asks the server to appraise an object. Payload: object id.</summary>
        public Task<bool> AppraiseAsync(uint objectId)
        {
            if (IsAvailable)
                Appraising?.Invoke(objectId);
            return SendAsync(GameActions.IdentifyObject, w => w.UInt32(objectId));
        }

        /// <summary>Uses an object. Payload: object id.</summary>
        public Task<bool> UseAsync(uint objectId)
            => SendAsync(GameActions.Use, w => w.UInt32(objectId));

        /// <summary>Uses one object on another. Payload: source id, target id.</summary>
        public Task<bool> UseOnAsync(uint sourceId, uint targetId)
            => SendAsync(GameActions.UseWithTarget, w => w.UInt32(sourceId).UInt32(targetId));

        /// <summary>
        /// Moves an item into a container. Payload: item id, container id, placement.
        /// </summary>
        public Task<bool> MoveToContainerAsync(uint objectId, uint containerId, int slot = 0)
            => SendAsync(GameActions.PutItemInContainer, w => w.UInt32(objectId).UInt32(containerId).Int32(slot));

        /// <summary>Drops an item. Payload: item id.</summary>
        public Task<bool> DropAsync(uint objectId)
            => SendAsync(GameActions.DropItem, w => w.UInt32(objectId));

        /// <summary>Speaks. Payload: the text, length-prefixed and padded.</summary>
        /// <remarks>
        /// ACE treats a leading '@' as a console command, so text is sent exactly as
        /// given and the caller decides whether that is wanted.
        /// </remarks>
        public Task<bool> SayAsync(string text)
            => SendAsync(GameActions.Talk, w => w.String(text ?? string.Empty));

        /// <summary>
        /// Casts a spell at something. Payload: the target, then the spell.
        /// </summary>
        /// <remarks>
        /// A self-buff is this with the character's own id as the target, which is what
        /// the client does. Untargeted casting is a different action; see
        /// <see cref="CastUntargetedAsync"/>.
        /// </remarks>
        public Task<bool> CastAsync(uint targetId, uint spellId)
            => SendAsync(GameActions.CastTargetedSpell, w => w.UInt32(targetId).UInt32(spellId));

        /// <summary>Casts a spell that takes no target. Payload: the spell.</summary>
        /// <remarks>
        /// Not yet seen on the wire here. The layout is ACE's handler's, which reads the one
        /// word and nothing more - the same spell word the targeted cast ends with.
        /// </remarks>
        public Task<bool> CastUntargetedAsync(uint spellId)
            => SendAsync(GameActions.CastUntargetedSpell, w => w.UInt32(spellId));

        /// <summary>
        /// Swings at a creature. Payload: the target, the attack height, then the power as a
        /// float from 0 to 1.
        /// </summary>
        /// <remarks>
        /// No recorded session has the character fighting with a weapon, so this is ACE's
        /// handler's read order: a word, a word, a float. The server clamps the power to 0-1
        /// itself, refuses the attack outside Melee stance, and walks a character that is
        /// out of reach to the target before swinging.
        /// </remarks>
        public Task<bool> MeleeAttackAsync(uint targetId, AttackHeight height, float power)
            => SendAsync(GameActions.TargetedMeleeAttack, w => w.UInt32(targetId).UInt32((uint)height).Single(power));

        /// <summary>
        /// Shoots at a creature. Payload: the target, the attack height, then the accuracy as
        /// a float from 0 to 1 - ACE's handler's read order, as for the melee attack.
        /// </summary>
        public Task<bool> MissileAttackAsync(uint targetId, AttackHeight height, float accuracy)
            => SendAsync(GameActions.TargetedMissileAttack, w => w.UInt32(targetId).UInt32((uint)height).Single(accuracy));

        /// <summary>Stops the attack under way. No payload: ACE's handler reads nothing.</summary>
        public Task<bool> CancelAttackAsync()
            => SendAsync(GameActions.CancelAttack, w => w);

        /// <summary>
        /// Wields an item in a slot. Payload: the item, then the slot as ACE's EquipMask.
        /// </summary>
        /// <remarks>
        /// ACE's handler's read order; the answer is the WieldObject event, which the world
        /// already follows, or a refusal. The slot must be free - the client takes the old
        /// weapon off first, and so must anything that asks for this.
        /// </remarks>
        public Task<bool> WieldAsync(uint objectId, uint slot)
            => SendAsync(GameActions.GetAndWieldItem, w => w.UInt32(objectId).UInt32(slot));

        /// <summary>
        /// Merges one stack into another. Payload: the source stack, the target stack, then the
        /// amount as a signed word.
        /// </summary>
        /// <remarks>
        /// No recorded session has the player merging stacks, so this is ACE's handler's read
        /// order (GameActionStackableMerge: two words and an int). ACE refuses an amount of
        /// zero or less, one larger than the source, and one that would overfill the target.
        /// </remarks>
        public Task<bool> StackableMergeAsync(uint fromStackId, uint toStackId, int amount)
            => SendAsync(GameActions.StackableMerge, w => w.UInt32(fromStackId).UInt32(toStackId).Int32(amount));

        /// <summary>
        /// Salvages items. Payload: the tool, a count, then each item.
        /// </summary>
        /// <remarks>
        /// No recorded session salvages, so this is ACE's handler's read order
        /// (GameActionCreateTinkeringTool: the tool's word, a count word, then that many item
        /// words). ACE needs no salvage panel open for it, checks the tool is an Ultimate
        /// Salvaging Tool in the packs, and skips any item without a material or workmanship.
        /// </remarks>
        public Task<bool> SalvageAsync(uint toolId, IReadOnlyList<uint> itemIds)
            => SendAsync(GameActions.CreateTinkeringTool, w =>
            {
                IReadOnlyList<uint> items = itemIds ?? Array.Empty<uint>();
                w.UInt32(toolId).UInt32((uint)items.Count);
                foreach (uint item in items)
                    w.UInt32(item);
                return w;
            });

        /// <summary>
        /// Hands an item to a player or NPC. Payload: the target, the item, then the amount as a
        /// signed word.
        /// </summary>
        /// <remarks>
        /// No recorded session gives anything, so this is ACE's handler's read order
        /// (GameActionGiveObjectRequest: two words and an int), the target first. ACE walks the
        /// character to the target and refuses an amount of zero or less, or more than the stack.
        /// </remarks>
        public Task<bool> GiveAsync(uint objectId, uint targetId, int amount)
            => SendAsync(GameActions.GiveObjectRequest, w => w.UInt32(targetId).UInt32(objectId).Int32(amount));

        /// <summary>Asks a player to join the fellowship. Payload: the player's id, as ACE's handler reads it.</summary>
        /// <remarks>
        /// ACE looks the player up among those online and refuses, in chat, one who is not, who is
        /// in a fellowship already, or who declines; and a recruit by anyone but the leader of a
        /// fellowship that is not open.
        /// </remarks>
        public Task<bool> FellowshipRecruitAsync(uint playerId)
            => SendAsync(GameActions.FellowshipRecruit, w => w.UInt32(playerId));

        /// <summary>
        /// Leaves the fellowship, or disbands it. Payload: a word, 1 to disband - ACE's
        /// FellowshipQuit, which disbands only for the leader and otherwise just leaves.
        /// </summary>
        public Task<bool> FellowshipQuitAsync(bool disband)
            => SendAsync(GameActions.FellowshipQuit, w => w.UInt32(disband ? 1u : 0u));

        /// <summary>Dismisses a member. Payload: the member's id.</summary>
        public Task<bool> FellowshipDismissAsync(uint playerId)
            => SendAsync(GameActions.FellowshipDismiss, w => w.UInt32(playerId));

        /// <summary>Hands the leadership to another member. Payload: the member's id.</summary>
        public Task<bool> FellowshipAssignLeaderAsync(uint playerId)
            => SendAsync(GameActions.FellowshipAssignNewLeader, w => w.UInt32(playerId));

        /// <summary>Opens the fellowship to recruiting by any member, or closes it. Payload: a word, 1 for open.</summary>
        public Task<bool> FellowshipSetOpenAsync(bool open)
            => SendAsync(GameActions.FellowshipChangeOpenness, w => w.UInt32(open ? 1u : 0u));

        /// <summary>
        /// Changes stance. Casting needs Magic; swinging needs Melee. The server refuses
        /// actions that do not match the stance, so this usually comes first.
        /// </summary>
        public Task<bool> SetCombatModeAsync(CombatMode mode)
            => SendAsync(GameActions.ChangeCombatMode, w => w.UInt32((uint)mode));

        /// <summary>
        /// Asks for a creature's health, which arrives as a fraction rather than a
        /// number - the server does not tell other players exact hit points.
        /// </summary>
        public Task<bool> QueryHealthAsync(uint objectId)
            => SendAsync(GameActions.QueryHealth, w => w.UInt32(objectId));

        /// <summary>
        /// Sets what the body is doing. Movement in this protocol is a state, not a
        /// step: the character keeps walking until a different state is sent, which is
        /// why stopping is its own call rather than the absence of one.
        /// </summary>
        /// <remarks>
        /// Fails rather than guesses if the client has not reported a position yet. The
        /// message carries the last position and sequences seen from the client, because
        /// the server checks them against its own view and discards movement computed
        /// against a world it has already replaced.
        /// </remarks>
        public Task<bool> MoveAsync(ClientMotionState motion)
        {
            if (motion == null) throw new ArgumentNullException(nameof(motion));

            if (_world == null || !_world.Character.Location.HasValue)
            {
                // Sending a made-up position is how a character ends up somewhere it
                // has never been.
                _log.Warn("Cannot move yet: the client has not reported a position.");
                Refused++;
                return Task.FromResult(false);
            }

            Location where = _world.Character.Location.Value;
            MovementSequences sequences = _world.Character.Sequences;

            return SendAsync(GameActions.MoveToState, w =>
            {
                WriteMotionState(w, motion);

                w.UInt32(where.LandblockCell)
                 .Single(where.X).Single(where.Y).Single(where.Z)
                 .Single(where.QW).Single(where.QX).Single(where.QY).Single(where.QZ);

                w.UInt16(sequences.Instance)
                 .UInt16(sequences.ServerControl)
                 .UInt16(sequences.Teleport)
                 .UInt16(sequences.ForcePosition)
                 .UInt32(sequences.Contact);

                return w;
            });
        }

        /// <summary>Walks forward at <paramref name="speed"/>, where 1 is the usual pace.</summary>
        public Task<bool> WalkForwardAsync(float speed = 1.0f)
            => MoveAsync(WithStyle(new ClientMotionState
            {
                Flags = MotionFlags.CurrentHoldKey | MotionFlags.CurrentStyle
                      | MotionFlags.ForwardCommand | MotionFlags.ForwardHoldKey | MotionFlags.ForwardSpeed,
                CurrentHoldKey = 2,
                ForwardCommand = MotionCommands.WalkForward,
                ForwardHoldKey = 1,
                ForwardSpeed = speed,
            }));

        /// <summary>Turns in place. Positive is right, negative is left.</summary>
        public Task<bool> TurnAsync(float speed = 1.0f)
            => MoveAsync(WithStyle(new ClientMotionState
            {
                Flags = MotionFlags.CurrentHoldKey | MotionFlags.CurrentStyle
                      | MotionFlags.TurnCommand | MotionFlags.TurnHoldKey | MotionFlags.TurnSpeed,
                CurrentHoldKey = 2,
                TurnCommand = speed >= 0 ? MotionCommands.TurnRight : MotionCommands.TurnLeft,
                TurnHoldKey = 1,
                TurnSpeed = Math.Abs(speed),
            }));

        /// <summary>Stops: the standing state the client sends when nothing is held.</summary>
        public Task<bool> StopAsync()
            => MoveAsync(WithStyle(new ClientMotionState
            {
                Flags = MotionFlags.CurrentHoldKey | MotionFlags.CurrentStyle,
                CurrentHoldKey = 1,
            }));

        /// <summary>
        /// Keeps the current stance. Sending the wrong style would change what the
        /// character is holding out, so the observed one is reused when it is known.
        /// </summary>
        private ClientMotionState WithStyle(ClientMotionState motion)
        {
            uint style = _world?.Character.Motion?.CurrentStyle ?? 0;
            motion.CurrentStyle = style != 0 ? style : MotionCommands.Ready;
            return motion;
        }

        private static void WriteMotionState(PayloadWriter w, ClientMotionState motion)
        {
            int actions = motion.Actions?.Count ?? 0;
            w.UInt32((motion.Flags & MotionFlags.Fields) | ((uint)actions << MotionFlags.ActionCountShift));

            if ((motion.Flags & MotionFlags.CurrentHoldKey) != 0) w.UInt32(motion.CurrentHoldKey);
            if ((motion.Flags & MotionFlags.CurrentStyle) != 0) w.UInt32(motion.CurrentStyle);
            if ((motion.Flags & MotionFlags.ForwardCommand) != 0) w.UInt32(motion.ForwardCommand);
            if ((motion.Flags & MotionFlags.ForwardHoldKey) != 0) w.UInt32(motion.ForwardHoldKey);
            if ((motion.Flags & MotionFlags.ForwardSpeed) != 0) w.Single(motion.ForwardSpeed);
            if ((motion.Flags & MotionFlags.SidestepCommand) != 0) w.UInt32(motion.SidestepCommand);
            if ((motion.Flags & MotionFlags.SidestepHoldKey) != 0) w.UInt32(motion.SidestepHoldKey);
            if ((motion.Flags & MotionFlags.SidestepSpeed) != 0) w.Single(motion.SidestepSpeed);
            if ((motion.Flags & MotionFlags.TurnCommand) != 0) w.UInt32(motion.TurnCommand);
            if ((motion.Flags & MotionFlags.TurnHoldKey) != 0) w.UInt32(motion.TurnHoldKey);
            if ((motion.Flags & MotionFlags.TurnSpeed) != 0) w.Single(motion.TurnSpeed);

            for (int i = 0; i < actions; i++)
            {
                ClientMotionAction action = motion.Actions[i];
                w.UInt16(action.Command).UInt16(action.Stamp).Single(action.Speed);
            }
        }

        /// <summary>The client's count of the one-off motions it has begun, which stamps each.</summary>
        private ushort _actionStamp;

        /// <summary>
        /// Plays one of the client's chat emotes - "*wave*", "*dance*" - as the client plays it: a
        /// MoveToState with the emote's motion command in it, standing where the client last said
        /// it stood. A one-off motion (a wave, 0x10000000 set) goes as an action after the fields;
        /// a lasting one (dancing, sitting, 0x40000000 set) as the forward command, until the
        /// character next moves.
        /// </summary>
        /// <remarks>
        /// The client's own motion interpreter put an emote there (CMotionInterp::DoMotion, then
        /// the raw state's ApplyMotion), and ACE reads it from there: an action only when it is a
        /// soul emote at speed 1, and a lasting one as the forward command it shows everyone. ACE
        /// plays the motion and sends it to everyone in sight - the character's own client too,
        /// but marked as the client's own doing, which the client may not play, since it did not
        /// begin it. The caller checks the stance first, as the client did.
        /// </remarks>
        public Task<bool> EmoteAsync(uint motionCommand)
        {
            ClientMotionState last = _world?.Character.Motion;
            ClientMotionState motion = WithStyle(new ClientMotionState
            {
                Flags = MotionFlags.CurrentHoldKey | MotionFlags.CurrentStyle,
                CurrentHoldKey = last != null && (last.Flags & MotionFlags.CurrentHoldKey) != 0 ? last.CurrentHoldKey : 1,
            });

            if ((motionCommand & LastingMotion) != 0)
            {
                motion.Flags |= MotionFlags.ForwardCommand;
                motion.ForwardCommand = motionCommand;
            }
            else
            {
                _actionStamp = (ushort)((_actionStamp + 1) & 0x7FFF);
                motion.Actions.Add(new ClientMotionAction((ushort)motionCommand, (ushort)(_actionStamp | ClientBegan), 1f));
            }

            return MoveAsync(motion);
        }

        /// <summary>A motion command's class bit for a lasting state - sitting, dancing - rather than a one-off action.</summary>
        private const uint LastingMotion = 0x40000000;

        /// <summary>An action stamp's top bit: the client began it.</summary>
        private const ushort ClientBegan = 0x8000;

        /// <summary>
        /// Sends what the game client sends for one of its chat-box commands
        /// (<see cref="ClientCommands.Parse"/>): a game action, or a whole message for the
        /// server's chat rooms. For a chat emote this is its words, the SoulEmote; its motion is
        /// <see cref="EmoteAsync"/>'s. False for a line that is not something to send.
        /// </summary>
        public async Task<bool> SendCommandAsync(ClientCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));

            if (command.Kind == ClientCommandKind.Action || command.Kind == ClientCommandKind.Emote)
                return await SendAsync(command.Type, w => w.Bytes(command.Fields)).ConfigureAwait(false);

            if (command.Kind != ClientCommandKind.Message)
                return false;

            return await SendMessageAsync(command.Type, command.Fields).ConfigureAwait(false);
        }

        // ------------------------------------------------------------------- the character list

        /// <summary>
        /// What logs the character out and brings one into the world for <see cref="LogOutAsync"/>
        /// and <see cref="EnterWorldAsync"/>: which way, when, and whether it worked. Set by the
        /// host; without it the logoff goes as the bare message and entering is refused.
        /// </summary>
        public SessionControl Session { get; set; }

        /// <summary>Logs the character out to the character list. See <see cref="IGameActions.LogOutAsync"/>.</summary>
        public Task<bool> LogOutAsync()
            => Session != null ? Session.LogOutAsync() : SendLogOffAsync();

        /// <summary>Enters the world as one of the account's characters. See <see cref="IGameActions.EnterWorldAsync"/>.</summary>
        public Task<bool> EnterWorldAsync(uint characterId)
        {
            if (Session != null)
                return Session.EnterWorldAsync(characterId);

            _log.Warn("Cannot enter the world from here: nothing is watching the character list.");
            Refused++;
            return Task.FromResult(false);
        }

        /// <summary>
        /// The client's own request to log off, as captured: CharacterLogOff, 0xF653, carrying
        /// nothing. The server plays the logging-out motion and, about six seconds later, answers
        /// with the same opcode, the character list and its name. Not a game action: it travels on
        /// its own, as the client sends it.
        /// </summary>
        public Task<bool> SendLogOffAsync()
            => SendMessageAsync(Opcodes.CharacterLogOff, Array.Empty<byte>());

        // Entering the world has no message here: AC:Unreal takes no login it did not ask for -
        // its character select stays up - so the character select itself is clicked
        // (SessionControl), and the client sends CharacterEnterWorldRequest and CharacterEnterWorld
        // as it always does.

        /// <summary>Sends a whole message of the client's - not a game action - when acting is allowed.</summary>
        private async Task<bool> SendMessageAsync(uint opcode, byte[] payload)
        {
            if (!IsAvailable)
            {
                Refused++;
                return false;
            }

            try
            {
                await _transport.SendAsync(AcMessage.Create(opcode, payload)).ConfigureAwait(false);
                Sent++;
                return true;
            }
            catch (Exception ex)
            {
                _log.Error($"Could not send message 0x{opcode:X4}.", ex);
                return false;
            }
        }

        /// <summary>
        /// The next ordering sequence to send, kept above anything the client has used.
        /// The client owns this counter and cannot know about an injected message, and a
        /// number the server has already been given is the one number certain to look
        /// like a repeat.
        /// </summary>
        private uint NextSequence()
        {
            uint seen = _world?.Character.LastActionSequence ?? 0;
            _sequence = Math.Max(seen, _sequence) + 1;
            return _sequence;
        }

        private async Task<bool> SendAsync(uint actionType, Func<PayloadWriter, PayloadWriter> build)
        {
            if (!IsAvailable)
            {
                Refused++;
                return false;
            }

            PayloadWriter writer = new PayloadWriter();
            writer.UInt32(NextSequence());
            writer.UInt32(actionType);
            build(writer);

            try
            {
                await _transport.SendAsync(AcMessage.Create(Opcodes.GameAction, writer.ToArray())).ConfigureAwait(false);
                Sent++;
                return true;
            }
            catch (Exception ex)
            {
                // A failed send is a normal outcome for a session that has just ended;
                // it must not propagate into whatever plugin asked for it.
                _log.Error($"Could not send game action 0x{actionType:X4}.", ex);
                return false;
            }
        }

        /// <summary>Builds an action payload in the protocol's own encoding.</summary>

    }
}
