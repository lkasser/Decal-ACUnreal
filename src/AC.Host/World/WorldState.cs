using System;
using System.Collections.Generic;

using AC.Host.Decoding;

namespace AC.Host.World
{
    /// <summary>Read access to the world for plugins.</summary>
    public interface IWorldView
    {
        /// <summary>Name the server announced, or null before connection.</summary>
        string ServerName { get; }

        int ObjectCount { get; }

        WorldObject Get(uint id);

        bool TryGet(uint id, out WorldObject obj);

        /// <summary>Every known object. Do not hold the enumeration across host callbacks.</summary>
        IEnumerable<WorldObject> Objects { get; }

        /// <summary>Objects whose container is <paramref name="containerId"/>.</summary>
        IEnumerable<WorldObject> ContentsOf(uint containerId);
    }

    /// <summary>
    /// The host's model of the world, mutated by message decoders and read by
    /// plugins. Raises an event for each kind of change.
    /// </summary>
    /// <remarks>
    /// All access is on the game thread. Events are raised synchronously from the
    /// mutation, after the state has been updated, so a handler always sees the new
    /// state and never a half-applied one.
    /// </remarks>
    public sealed partial class WorldState : IWorldView
    {
        private readonly Dictionary<uint, WorldObject> _objects = new Dictionary<uint, WorldObject>();
        private Func<DateTimeOffset> _clock;

        public WorldState(Func<DateTimeOffset> clock = null)
        {
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
        }

        public CharacterState Character { get; } = new CharacterState();

        /// <summary>
        /// The client's data files. Assigning it also gives the character access, so
        /// skill values start using the game's own formulas.
        /// </summary>
        public IGameData GameData
        {
            get => _gameData;
            set
            {
                _gameData = value ?? NullGameData.Instance;
                Character.GameData = _gameData;
            }
        }

        private IGameData _gameData = NullGameData.Instance;

        public string ServerName { get; private set; }

        public int ObjectCount => _objects.Count;

        public IEnumerable<WorldObject> Objects => _objects.Values;

        public event EventHandler<string> ServerConnected;

        public event EventHandler<uint> PlayerIdentified;

        public event EventHandler<WorldObject> ObjectCreated;

        public event EventHandler<WorldObject> ObjectUpdated;

        public event EventHandler<WorldObject> ObjectAppraised;

        public event EventHandler<uint> ObjectRemoved;

        public event EventHandler<ContainerContents> ContainerViewed;

        public event EventHandler<ChatMessage> ChatReceived;

        public event EventHandler CharacterUpdated;

        /// <summary>An object started, changed or stopped a motion.</summary>
        public event EventHandler<WorldObject> ObjectMoved;

        public WorldObject Get(uint id) => _objects.TryGetValue(id, out WorldObject obj) ? obj : null;

        public bool TryGet(uint id, out WorldObject obj) => _objects.TryGetValue(id, out obj);

        public IEnumerable<WorldObject> ContentsOf(uint containerId)
        {
            foreach (WorldObject obj in _objects.Values)
            {
                if (obj.ContainerId == containerId)
                    yield return obj;
            }
        }

        /// <summary>
        /// The object with this id, created if unknown. Decoders use this so that a
        /// property update arriving before (or without) a creation message still has
        /// somewhere to land.
        /// </summary>
        internal WorldObject GetOrAdd(uint id, out bool created)
        {
            if (_objects.TryGetValue(id, out WorldObject obj))
            {
                created = false;
                return obj;
            }

            obj = new WorldObject(id);
            _objects[id] = obj;
            created = true;

            if (Character.Id == id)
                Character.Object = obj;

            return obj;
        }

        /// <summary>Raised when something lands a blow on the character.</summary>
        public event EventHandler<DamageTaken> DamageTaken;

        /// <summary>Raised when the character evades an attack. Carries the attacker's name.</summary>
        public event EventHandler<string> AttackEvaded;

        /// <summary>Raised when an enchantment is added or refreshed.</summary>
        public event EventHandler<Enchantment> EnchantmentChanged;

        /// <summary>Raised when an enchantment goes, with its packed spell id and layer.</summary>
        public event EventHandler<uint> EnchantmentRemoved;

        /// <summary>Raised when an action finishes, with the server's error or zero.</summary>
        public event EventHandler<uint> UseFinished;

        /// <summary>Raised when a container on the ground closes. Carries its id.</summary>
        public event EventHandler<uint> ContainerClosed;

        /// <summary>Raised when the server refuses to move an item, with the item and its error.</summary>
        public event EventHandler<MoveRefusal> MoveRefused;

        /// <summary>Raised when an attack the character made finishes, with the server's error or zero.</summary>
        public event EventHandler<uint> AttackFinished;

        /// <summary>Raised when a blow the character struck lands.</summary>
        public event EventHandler<DamageDealt> DamageDealt;

        /// <summary>Raised when something the character attacked evades. Carries its name.</summary>
        public event EventHandler<string> TargetEvaded;

        /// <summary>Raised when the next swing or shot of a repeating attack begins.</summary>
        public event EventHandler AttackCommenced;

        internal void SetServerName(string name)
        {
            ServerName = name;
            ServerConnected?.Invoke(this, name);
        }

        internal void SetPlayerId(uint id)
        {
            Character.Id = id;
            Character.Object = Get(id);
            if (Character.Object?.Location is Location where)
                ViewFrom(where.LandblockCell);

            PlayerIdentified?.Invoke(this, id);
        }

        /// <summary>Announces a creation or full re-send that a decoder has already applied.</summary>
        /// <summary>
        /// Notes an ordering sequence the client has used. Kept as a high-water mark:
        /// the numbers are not strictly consecutive across action types, and what
        /// matters is only never to send one the server has already been given.
        /// </summary>
        internal void NoteClientActionSequence(uint sequence)
        {
            if (sequence > Character.LastActionSequence)
                Character.LastActionSequence = sequence;
        }

        /// <summary>
        /// Where the client says it is. The server does not echo a player's own movement
        /// back to them, so this is the only running account of it.
        /// </summary>
        internal void SetClientPosition(Location location, MovementSequences sequences)
        {
            Character.Location = location;
            Character.Sequences = sequences;
            ViewFrom(location.LandblockCell);
            CheckVendorReach();
            NotifyCharacterUpdated();
        }

        internal void SetClientSelection(uint objectId)
        {
            if (Character.SelectedId == objectId)
                return;

            Character.SelectedId = objectId;
            NotifyCharacterUpdated();
        }

        internal void SetClientMotion(ClientMotionState motion, Location location, MovementSequences sequences)
        {
            Character.Motion = motion;
            Character.Location = location;
            Character.Sequences = sequences;
            ViewFrom(location.LandblockCell);
            CheckVendorReach();
            NotifyCharacterUpdated();
        }

        internal void NotifyCreated(WorldObject obj)
        {
            obj.LastSeen = _clock();
            obj.CreateCount++;
            obj.ArrivalOrder = WorldObject.NextArrival();
            NoteWhereabouts(obj);
            ObjectCreated?.Invoke(this, obj);
        }

        internal void NotifyUpdated(WorldObject obj)
        {
            obj.LastSeen = _clock();
            NoteWhereabouts(obj);
            ObjectUpdated?.Invoke(this, obj);
        }

        internal void NotifyAppraised(WorldObject obj)
        {
            obj.LastSeen = _clock();
            ObjectAppraised?.Invoke(this, obj);
        }

        internal void Remove(uint id)
        {
            if (!_objects.Remove(id))
                return;

            _outOfView.Remove(id);

            // Anything that claimed to be inside the removed container is stale.
            foreach (WorldObject obj in _objects.Values)
            {
                if (obj.ContainerId == id)
                    obj.ContainerId = null;
            }

            ObjectRemoved?.Invoke(this, id);

            // A vendor gone from view is a vendor whose window has closed.
            if (Character.OpenVendorId == id)
                CloseVendor();
        }

        internal void NotifyContainerViewed(ContainerContents contents)
        {
            foreach (ContainedItem item in contents.Items)
            {
                WorldObject obj = GetOrAdd(item.Id, out _);
                obj.ContainerId = contents.ContainerId;
            }

            ContainerViewed?.Invoke(this, contents);

            // A chest or corpse opened takes the place a vendor's window had, as the server's
            // one "last opened container" does; the character's own packs do not.
            if (Character.OpenVendorId != 0 && TryGet(contents.ContainerId, out WorldObject opened)
                && !opened.ContainerId.HasValue && !opened.WielderId.HasValue && opened.Id != Character.Id)
                CloseVendor();
        }

        internal void NotifyMoved(WorldObject obj)
        {
            obj.LastSeen = _clock();
            ObjectMoved?.Invoke(this, obj);
        }

        /// <summary>
        /// An action the client asked for has finished. Zero means it worked; anything
        /// else is the error the server ended it with.
        /// </summary>
        internal void NotifyUseFinished(uint error)
        {
            UseFinished?.Invoke(this, error);
        }

        /// <summary>A container on the ground has closed, so its contents are out of reach.</summary>
        internal void NotifyContainerClosed(uint containerId)
        {
            ContainerClosed?.Invoke(this, containerId);
        }

        /// <summary>The server would not move an item; it is still where it was.</summary>
        internal void NotifyMoveRefused(MoveRefusal refusal)
        {
            MoveRefused?.Invoke(this, refusal);
        }

        internal void NotifyDamageTaken(DamageTaken damage)
        {
            DamageTaken?.Invoke(this, damage);
        }

        internal void NotifyAttackEvaded(string attacker)
        {
            AttackEvaded?.Invoke(this, attacker);
        }

        internal void NotifyAttackFinished(uint error)
        {
            AttackFinished?.Invoke(this, error);
        }

        internal void NotifyDamageDealt(DamageDealt damage)
        {
            DamageDealt?.Invoke(this, damage);
        }

        internal void NotifyTargetEvaded(string defender)
        {
            TargetEvaded?.Invoke(this, defender);
        }

        internal void NotifyAttackCommenced()
        {
            AttackCommenced?.Invoke(this, EventArgs.Empty);
        }

        internal void SetEnchantment(Enchantment enchantment)
        {
            Character.SetEnchantment(enchantment);
            EnchantmentChanged?.Invoke(this, enchantment);
            NotifyCharacterUpdated();
        }

        internal void RemoveEnchantment(uint packedId)
        {
            // Worth announcing even if it was never seen arriving: a session joined late
            // still learns that something has gone.
            Character.RemoveEnchantment(packedId);
            EnchantmentRemoved?.Invoke(this, packedId);
            NotifyCharacterUpdated();
        }

        /// <summary>Several enchantments added or refreshed at once, announced one by one and then once as a whole.</summary>
        internal void SetEnchantments(IReadOnlyList<Enchantment> enchantments)
        {
            foreach (Enchantment enchantment in enchantments)
            {
                Character.SetEnchantment(enchantment);
                EnchantmentChanged?.Invoke(this, enchantment);
            }

            NotifyCharacterUpdated();
        }

        /// <summary>
        /// The whole set, as a login describes it. Anything the host had that is not in it
        /// has gone, and is announced as gone.
        /// </summary>
        internal void ReplaceEnchantments(IReadOnlyList<Enchantment> enchantments)
        {
            HashSet<uint> kept = new HashSet<uint>();
            foreach (Enchantment enchantment in enchantments)
                kept.Add(enchantment.PackedId);

            List<uint> gone = new List<uint>();
            foreach (uint packedId in Character.Enchantments.Keys)
            {
                if (!kept.Contains(packedId))
                    gone.Add(packedId);
            }

            foreach (uint packedId in gone)
            {
                Character.RemoveEnchantment(packedId);
                EnchantmentRemoved?.Invoke(this, packedId);
            }

            SetEnchantments(enchantments);
        }

        /// <summary>Several enchantments gone at once.</summary>
        internal void RemoveEnchantments(IReadOnlyList<uint> packedIds)
        {
            foreach (uint packedId in packedIds)
            {
                Character.RemoveEnchantment(packedId);
                EnchantmentRemoved?.Invoke(this, packedId);
            }

            NotifyCharacterUpdated();
        }

        /// <summary>
        /// Death's purge. ACE takes away every enchantment except those that never run out -
        /// what worn items give - and the cooldowns, whose spell ids are above 0x7FFF; the
        /// client, told only that a purge happened, is left to do the same.
        /// </summary>
        internal void PurgeEnchantments()
        {
            HashSet<ushort> exempt = new HashSet<ushort>();
            foreach (Enchantment e in Character.Enchantments.Values)
            {
                if (e.Duration == -1 || e.SpellId > 0x7FFF)
                    exempt.Add(e.SpellId);
            }

            List<uint> gone = new List<uint>();
            foreach (Enchantment e in Character.Enchantments.Values)
            {
                if (!exempt.Contains(e.SpellId))
                    gone.Add(e.PackedId);
            }

            RemoveEnchantments(gone);
        }

        /// <summary>The spellbook as a login describes it.</summary>
        internal void SetSpellbook(IEnumerable<uint> spells)
        {
            Character.ReplaceSpellbook(spells);
            NotifyCharacterUpdated();
        }

        internal void LearnSpell(uint spellId)
        {
            Character.LearnSpell(spellId);
            NotifyCharacterUpdated();
        }

        internal void ForgetSpell(uint spellId)
        {
            Character.ForgetSpell(spellId);
            NotifyCharacterUpdated();
        }

        /// <summary>The player cast a spell - their own cast, read from what the client sent.</summary>
        internal void NoteClientCast(uint spellId)
        {
            Character.LastCastSpellId = spellId;
            NotifyCharacterUpdated();
        }

        /// <summary>The fellowship described in full: exactly these members, stamped now.</summary>
        internal void SetFellowship(string name, uint leaderId, IReadOnlyList<Fellow> members)
        {
            foreach (Fellow fellow in members)
                fellow.LastUpdated = _clock();

            Character.FellowshipData.Replace(name, leaderId, members);
            NotifyCharacterUpdated();
        }

        /// <summary>One member described again - most often only their vitals changing.</summary>
        internal void UpdateFellow(Fellow fellow)
        {
            fellow.LastUpdated = _clock();
            Character.FellowshipData.Update(fellow);
            NotifyCharacterUpdated();
        }

        /// <summary>
        /// Someone left the fellowship or was dismissed from it. When it is the character, the
        /// character has no fellowship any more.
        /// </summary>
        internal void RemoveFellow(uint id)
        {
            if (id != 0 && id == Character.Id)
                Character.FellowshipData.Clear();
            else
                Character.FellowshipData.Remove(id);

            NotifyCharacterUpdated();
        }

        internal void DisbandFellowship()
        {
            Character.FellowshipData.Clear();
            NotifyCharacterUpdated();
        }

        /// <summary>The client said its fellowship panel opened or closed.</summary>
        internal void NoteFellowshipPanel(bool open)
        {
            Character.FellowshipData.PanelOpen = open;
            NotifyCharacterUpdated();
        }

        internal void NotifyChat(ChatMessage message)
        {
            if (message.Kind == ChatKind.Tell)
                NoteTellFrom(message.SenderName);

            ChatReceived?.Invoke(this, message);
        }

        internal void NotifyCharacterUpdated()
        {
            CharacterUpdated?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Forgets every object. Used when the session ends or restarts.</summary>
        internal void Clear()
        {
            List<uint> ids = new List<uint>(_objects.Keys);
            _objects.Clear();
            ForgetView();

            foreach (uint id in ids)
                ObjectRemoved?.Invoke(this, id);
        }
    }
}
