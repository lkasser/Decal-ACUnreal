using System;
using System.Collections.Generic;
using System.Linq;
using AC.Host.Decoding;
using AC.Host.Handover;
using AC.Host.Transport;
using AC.Host.World;
using AC.Protocol;

namespace AC.Host
{
    /// <summary>
    /// Carrying a session on from the host before this one, and keeping what the next one needs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A host stopped and started again while the game stays connected - the Decal Agent updated,
    /// say - finds the session going on as if nothing had happened: the relay binds the same
    /// ports, the server sees the same endpoint, the client never notices. But everything the
    /// server says once, at login - who the character is, what it carries, what is around it,
    /// the server's name - went by before the new host was there to hear it, and plugins cannot
    /// work without it.
    /// </para>
    /// <para>
    /// So every host keeps the messages its world was built from (<see cref="SessionJournal"/>),
    /// and hands them over when it stops properly. The next host replays them through its own
    /// decoders before it applies the first message it relays, with nothing said to plugins while
    /// it does; then it tells plugins what a login tells them, in a login's order - the server,
    /// the character, every object, the enchantments, then the messages themselves - and Decal's
    /// plugins hear Login, their ServerDispatch the login's messages, and LoginComplete from that,
    /// as they would at a login. To a plugin, the player has just logged in.
    /// </para>
    /// <para>
    /// With nothing handed over - the host before crashed, or there was none - the host knows it
    /// joined in the middle, says so in its log and, once, in the game, and asks the player to go
    /// back to the character list and in again, which tells it everything afresh.
    /// </para>
    /// </remarks>
    public sealed partial class GameHost
    {
        /// <summary>What the player is told, once, when the host joined a session with nothing handed over.</summary>
        public const string JoinedMidSessionLine
            = "Decal Agent started while you were in the world: log out to character select and enter the world again so it can see your character.";

        private readonly SessionJournal _journal = new SessionJournal();
        private HandoverSnapshot _handover;
        private bool _replaying;
        private bool _toldJoinedMidSession;
        private bool _saidJournalFull;

        /// <summary>Whether a session is going on: from the first traffic, or the first message, until it ends.</summary>
        private bool _sessionUnderWay;

        /// <summary>Whether a message is being applied, so that what the world does is that message's doing.</summary>
        private bool _applyingMessage;

        /// <summary>The messages this session's world was built from, for the next host. Game thread only.</summary>
        public SessionJournal SessionJournal => _journal;

        /// <summary>
        /// True while the host knows only part of the session: it joined one already under way,
        /// with nothing handed over to say what had gone before, and has not yet seen the character
        /// enter the world. The player has been asked to go back to the character list and in again.
        /// </summary>
        public bool JoinedMidSession { get; private set; }

        /// <summary>When the host this one carried the session on from stopped, or null when it did not carry one on.</summary>
        public DateTimeOffset? CarriedOnFrom { get; private set; }

        /// <summary>
        /// Where the host looked for a session the host before it handed over, and what it found
        /// there, in a few words for <c>achost ctl status</c> - "none at", "taken from", or why what
        /// was there was not taken. Null when it did not look: a replay, or a host told not to.
        /// Set by whatever looked, before the host starts.
        /// </summary>
        public string HandoverLookup { get; set; }

        /// <summary>
        /// What became of the session offered with <see cref="OfferHandover"/>, in a few words:
        /// waiting for the game to be heard from, carried on, or let go and why. Null when none was
        /// offered. Game thread only.
        /// </summary>
        public string HandoverFate { get; private set; }

        /// <summary>
        /// Gives the host what the host before it handed over. Taken up if the first traffic is the
        /// same session going on; a new login, or a snapshot gone stale, and it is let go. Before
        /// <see cref="StartAsync"/>.
        /// </summary>
        public void OfferHandover(HandoverSnapshot snapshot)
        {
            if (_started) throw new InvalidOperationException("A session is handed over before the host starts.");

            _handover = snapshot;
            HandoverFate = snapshot != null ? "to be carried on once the game is heard from" : null;
        }

        /// <summary>
        /// What this host hands to the next: the messages its world was built from, and what it keeps
        /// besides - or, when the game was never heard from, what the host before it handed over,
        /// still untaken. Null when there is nothing to hand over - no session going on, or one that
        /// outgrew the journal. On the game thread, or once the host has ended; whoever writes it
        /// adds the relay's endpoints and state.
        /// </summary>
        public HandoverSnapshot CreateHandover(DateTimeOffset now)
        {
            // Stopped before the game was heard from, still holding what the host before it left:
            // that goes on to the next host as it came, its age and all, rather than being lost
            // between two restarts.
            if (!_sessionUnderWay && _handover != null)
                return _handover;

            if (!_sessionUnderWay || _journal.Overflowed)
                return null;

            HandoverSnapshot snapshot = new HandoverSnapshot
            {
                WrittenAt = now,
                ActionsAllowed = _actionsAllowed,
                JoinedMidSession = JoinedMidSession,
                ToldJoinedMidSession = _toldJoinedMidSession,
                LastTellFrom = _world.LastTellFrom,
                LastTellTo = _world.LastTellTo,
                LastActionSequence = _world.Character.LastActionSequence,
            };

            snapshot.Journal.AddRange(_journal.Entries);
            return snapshot;
        }

        /// <summary>
        /// The transport's first packet. Raised before that packet's messages, so what this queues
        /// runs before the first of them is applied.
        /// </summary>
        private void OnSessionStarted(object sender, SessionStart start) => RunOnGameThread(() => BeginSession(start));

        /// <summary>Game thread only.</summary>
        private void BeginSession(SessionStart start)
        {
            HandoverSnapshot handover = _handover;
            _handover = null;
            _sessionUnderWay = true;

            if (start == SessionStart.Login)
            {
                if (handover != null)
                {
                    HandoverFate = "let go: the client began a new login";
                    _log.Info("The client began a new login, so the session the host before this one handed over is not carried on.");
                }

                return;
            }

            TimeSpan waited = handover != null ? _world.Now - handover.WrittenAt : TimeSpan.Zero;
            if (handover != null && waited <= HandoverSnapshot.MaxAge)
            {
                CarryOn(handover);
                return;
            }

            JoinedMidSession = true;
            const string WithoutIt = "Until the character enters the world again it does not know the character, what it carries, "
                + "what is around it or the server's name, and plugins work without them. Log out to the character list and enter the world again.";

            if (handover != null)
            {
                // Taken in time, but the game was not heard from until too late: as good as none.
                HandoverFate = $"let go: the game was first heard from {waited.TotalSeconds:0} s after it was written, too late to carry on";
                _log.Warn($"The host joined a session already under way, and the session the host before it handed over at {handover.WrittenAt.ToLocalTime():HH:mm:ss} "
                    + $"was {waited.TotalSeconds:0} s old by the time the game was first heard from - more than the {HandoverSnapshot.MaxAge.TotalMinutes:0} minutes "
                    + "a session can be carried on after - so it was let go. " + WithoutIt);
                return;
            }

            _log.Warn("The host joined a session already under way - it started while the game was connected - and nothing was handed over "
                + "from a host before it. " + WithoutIt);
        }

        /// <summary>
        /// Rebuilds the world from what was handed over, then tells plugins what a login tells them.
        /// Game thread only, before the first message relayed is applied.
        /// </summary>
        private void CarryOn(HandoverSnapshot snapshot)
        {
            DateTimeOffset now = _world.Now;

            // When each enchantment was last heard of, for ageing it below.
            Dictionary<uint, DateTimeOffset> heardAt = new Dictionary<uint, DateTimeOffset>();
            DateTimeOffset replayingFrom = default;
            EventHandler<Enchantment> noteHeard = (_, enchantment) => heardAt[enchantment.PackedId] = replayingFrom;
            int malformed = 0;

            _replaying = true;
            _world.EnchantmentChanged += noteHeard;
            try
            {
                // On the world's clock as it was when each message was first heard, so that what it
                // stamps - when an object went out of view, above all - is as it was.
                _world.ReplayWith(() => replayingFrom, () =>
                {
                    foreach (JournalEntry entry in snapshot.Journal)
                    {
                        replayingFrom = entry.At;
                        uint vendorBefore = _world.Character.OpenVendorId;
                        _applyingMessage = true;
                        try
                        {
                            if (MessageDecoder.Apply(entry.ToMessage(), entry.Direction, _world) == DecodeOutcome.Malformed)
                                malformed++;
                        }
                        catch (Exception ex)
                        {
                            malformed++;
                            _log.Error($"Decoder threw on opcode 0x{entry.Opcode:X4} replaying the session handed over.", ex);
                        }
                        finally
                        {
                            _applyingMessage = false;
                        }

                        // Kept again, as it was first heard, for the host after this one.
                        _journal.Record(new JournalEntry(entry.At, entry.Direction, entry.Opcode, entry.Payload, vendorBefore != _world.Character.OpenVendorId));
                    }
                });

                // What has been out of view long enough is let go of before anything is said of it:
                // the client let go of it while no host was there to see.
                _world.ForgetOutOfView();
            }
            finally
            {
                _world.EnchantmentChanged -= noteHeard;
                _replaying = false;
            }

            // An enchantment says how long it had left when the server sent it, and a plugin counts
            // down from when it heard - which is now. So each is aged by the time since it was sent,
            // as though the server had sent it this moment.
            foreach (Enchantment enchantment in _world.Character.Enchantments.Values)
            {
                if (!enchantment.IsPermanent && heardAt.TryGetValue(enchantment.PackedId, out DateTimeOffset at) && now > at)
                    enchantment.StartTime -= (now - at).TotalSeconds;
            }

            // What no message says again.
            if (!string.IsNullOrEmpty(snapshot.LastTellFrom))
                _world.NoteTellFrom(snapshot.LastTellFrom);
            if (!string.IsNullOrEmpty(snapshot.LastTellTo))
                _world.LastTellTo = snapshot.LastTellTo;
            _world.NoteClientActionSequence(snapshot.LastActionSequence);
            JoinedMidSession = snapshot.JoinedMidSession;
            _toldJoinedMidSession = snapshot.ToldJoinedMidSession;
            CarriedOnFrom = snapshot.WrittenAt;
            HandoverFate = "carried on";

            CharacterState character = _world.Character;
            _log.Info($"Carried on the session the host before this one handed over at {snapshot.WrittenAt.ToLocalTime():HH:mm:ss}: "
                + $"{snapshot.Journal.Count} message(s) replayed"
                + (malformed > 0 ? $" ({malformed} not understood)" : string.Empty)
                + (character.Id != 0
                    ? $", {(string.IsNullOrEmpty(character.Name) ? $"0x{character.Id:X8}" : character.Name)} in the world with {_world.ObjectCount} object(s) known."
                    : ", with no character in the world."));

            // Plugins act, or not, as the player last left it.
            if (CanAct)
                ActionsAllowed = snapshot.ActionsAllowed;

            AnnounceLogin(_journal.Entries);
        }

        /// <summary>
        /// Tells plugins what a login tells them, in a login's order, about the world as it now is;
        /// and gives <see cref="MessageSeen"/> the messages it was built from again, as a login
        /// gives them, before the character is said to be whole. Game thread only.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The messages are for Decal's plugins, many of which never look at Decal's filters: they
        /// keep their own track of the character and what it carries from the messages themselves,
        /// through ServerDispatch, and build it afresh from the ones a login brings - the character's
        /// description, every object created. Without them a plugin carried on knows nothing, and
        /// says so: Virindi Global Inventory tracking no items at all. So each one goes by again,
        /// in the order it first did, after Decal's Login and the objects and before LoginComplete -
        /// where Decal's own plugins heard a login's messages, between the character arriving and
        /// the client saying it had finished.
        /// </para>
        /// <para>
        /// Only what a login would bring again, or what changed what it brings since: not what
        /// told of a moment (see <see cref="RetoldOnCarryingOn"/>), which a plugin would take as
        /// happening now - a tell relayed twice, a kill counted again.
        /// </para>
        /// </remarks>
        private void AnnounceLogin(IReadOnlyList<JournalEntry> builtFrom)
        {
            if (!string.IsNullOrEmpty(_world.ServerName))
                Raise(ServerConnected, _world.ServerName);

            CharacterState character = _world.Character;
            if (character.Id != 0)
            {
                // At a login the character is named before its object is described, so the object
                // stays out of sight here, and Decal's Login waits for it, told of next, as Decal's
                // waited for the description. Decal's LoginComplete waits for the client's word that
                // it had entered the world, which is retold below with the login's messages, in its order.
                WorldObject self = character.Object;
                character.Object = null;
                try
                {
                    Raise(PlayerIdentified, character.Id);
                }
                finally
                {
                    character.Object = self;
                }
            }

            foreach (WorldObject obj in InLoginOrder())
                Raise(ObjectCreated, obj);

            foreach (Enchantment enchantment in character.Enchantments.Values.ToList())
                Raise(EnchantmentChanged, enchantment);

            if (character.InPortalSpace)
                Raise(PortalSpaceChanged, true);

            if (character.OpenVendorId != 0)
                Raise(VendorChanged, character.OpenVendorId);

            if (MessageSeen != null)
            {
                foreach (JournalEntry entry in builtFrom)
                {
                    if (RetoldOnCarryingOn(entry))
                        Raise(MessageSeen, new GameMessageEventArgs(entry.Direction, entry.ToMessage()));
                }
            }

            RaisePlain(CharacterUpdated);
        }

        /// <summary>
        /// Whether a message the world was built from is given to plugins again when the session is
        /// carried on: whatever a login would bring again, or changed what it brings - and not what
        /// only told of a moment, which no login repeats.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Of the server's: not what was said - speech, tells, emotes, the server's own lines, the
        /// rooms' chat, errors - nor combat's notices and deaths, an action's end, nor sounds and
        /// effects. Everything else is the state of things: objects and their properties, the
        /// character's description and enchantments, what moved where.
        /// </para>
        /// <para>
        /// Of the client's: what it sends to enter the world, and its word that it has - the
        /// action Decal's plugins wait for before they trust what they have heard. What the player
        /// did since - spoke, used, cast, moved - is not done again.
        /// </para>
        /// </remarks>
        internal static bool RetoldOnCarryingOn(JournalEntry entry)
        {
            AcMessage message = entry.ToMessage();
            if (entry.Direction == PacketDirection.Outbound)
            {
                if (message.Opcode == Opcodes.TurbineChat)
                    return false;

                return message.Opcode != Opcodes.GameAction
                    || (MessageDecoder.TryReadActionType(message, out uint action) && action == GameActions.LoginComplete);
            }

            switch (message.Opcode)
            {
                case Opcodes.HearSpeech:
                case Opcodes.HearRangedSpeech:
                case Opcodes.EmoteText:
                case Opcodes.SoulEmote:
                case Opcodes.ServerMessage:
                case Opcodes.TurbineChat:
                case Opcodes.PlayerKilled:
                case Opcodes.Sound:
                case Opcodes.PlayEffect:
                    return false;

                case Opcodes.GameEvent:
                    if (!MessageDecoder.TryReadGameEventType(message, out uint gameEvent))
                        return true;

                    switch (gameEvent)
                    {
                        case GameEvents.Tell:
                        case GameEvents.ChannelBroadcast:
                        case GameEvents.CommunicationTransientString:
                        case GameEvents.WeenieError:
                        case GameEvents.WeenieErrorWithString:
                        case GameEvents.AttackDone:
                        case GameEvents.VictimNotification:
                        case GameEvents.KillerNotification:
                        case GameEvents.AttackerNotification:
                        case GameEvents.DefenderNotification:
                        case GameEvents.EvasionAttackerNotification:
                        case GameEvents.EvasionDefenderNotification:
                        case GameEvents.CombatCommenceAttack:
                        case GameEvents.UseDone:
                        case GameEvents.InventoryServerSaveFailed:
                            return false;
                        default:
                            return true;
                    }

                default:
                    return true;
            }
        }

        /// <summary>
        /// Every object, in the order a login describes them: the character, then what it carries
        /// and wears, then everything around it.
        /// </summary>
        private List<WorldObject> InLoginOrder()
        {
            uint me = _world.Character.Id;
            List<WorldObject> self = new List<WorldObject>(1);
            List<WorldObject> carried = new List<WorldObject>();
            List<WorldObject> around = new List<WorldObject>();

            foreach (WorldObject obj in _world.Objects)
            {
                if (me != 0 && obj.Id == me)
                    self.Add(obj);
                else if (me != 0 && HeldBy(obj, me))
                    carried.Add(obj);
                else
                    around.Add(obj);
            }

            self.AddRange(carried);
            self.AddRange(around);
            return self;
        }

        /// <summary>Whether <paramref name="holderId"/> wields an object, or has it in a pack - or in a pack in a pack.</summary>
        private bool HeldBy(WorldObject obj, uint holderId)
        {
            // Two steps reach anything in a side pack; a few more cost nothing and end a loop.
            for (int step = 0; obj != null && step < 4; step++)
            {
                uint? holder = obj.WielderId ?? obj.ContainerId;
                if (!holder.HasValue)
                    return false;
                if (holder.Value == holderId)
                    return true;

                obj = _world.Get(holder.Value);
            }

            return false;
        }

        /// <summary>
        /// Keeps a message the host has just applied for the next host, and watches for the end of
        /// not knowing the session. Game thread only.
        /// </summary>
        private void Journal(PacketDirection direction, AcMessage message, uint vendorBefore)
        {
            _sessionUnderWay = true;
            _journal.Record(new JournalEntry(
                _world.Now,
                direction,
                message.Opcode,
                message.Payload.ToArray(),
                pinned: vendorBefore != _world.Character.OpenVendorId));

            if (_journal.Overflowed && !_saidJournalFull)
            {
                _saidJournalFull = true;
                _log.Warn($"This session has gone on too long to keep for handing over (more than {SessionJournal.MaxBytes / (1024 * 1024)} MB of messages); "
                    + "a host started after this one while the game is connected will have to be shown the character again.");
            }

            if (!JoinedMidSession)
                return;

            // The character entering the world: everything about it is about to be said in full.
            if (direction == PacketDirection.Inbound && message.Opcode == Opcodes.PlayerCreate)
            {
                JoinedMidSession = false;
                _log.Info("The character is entering the world, so the host sees the session whole from here.");
                return;
            }

            if (!_toldJoinedMidSession && InWorld(message))
            {
                _toldJoinedMidSession = true;
                ShowInGame(JoinedMidSessionLine);
            }
        }

        /// <summary>
        /// Keeps, as the deletion it amounts to, an object the world let go of on its own rather than
        /// as a message's doing - one out of view long enough, let go of on the tick - so that the
        /// next host lets go of it at the same point. A deletion of what is gone already is nothing,
        /// so one too many does no harm.
        /// </summary>
        private void JournalRemoval(uint id)
        {
            if (_applyingMessage)
                return;

            // ObjectDelete as ACE writes it: the object, then its instance sequence.
            byte[] delete = new byte[6];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(delete, id);
            _journal.Record(new JournalEntry(_world.Now, PacketDirection.Inbound, Opcodes.ObjectDelete, delete));
        }

        /// <summary>
        /// Whether a message means a character is in the world - where a line in the chat window can
        /// be read, as it cannot at the character list.
        /// </summary>
        private bool InWorld(AcMessage message)
            => _world.Character.Id != 0
               || message.Opcode == Opcodes.GameAction
               || message.Opcode == Opcodes.GameEvent
               || message.Opcode == Opcodes.ObjectCreate
               || message.Opcode == Opcodes.UpdatePosition
               || message.Opcode == Opcodes.Motion;

        /// <summary>The character left the world: of what built it, only the server's name still matters.</summary>
        private void ForgetCharacterInJournal()
            => _journal.KeepOnly(entry => entry.Direction == PacketDirection.Inbound && entry.Opcode == Opcodes.ServerName);

        /// <summary>The session ended: the next one, from its login, is seen whole.</summary>
        private void ForgetSessionInJournal()
        {
            _journal.Clear();
            _sessionUnderWay = false;
            _saidJournalFull = false;
            JoinedMidSession = false;
            _toldJoinedMidSession = false;
            CarriedOnFrom = null;
        }
    }
}
