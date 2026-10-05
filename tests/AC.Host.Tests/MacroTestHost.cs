using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AC.Host.Decoding;
using AC.Host.Plugins;
using AC.Host.World;

namespace AC.Host.Tests
{
    /// <summary>
    /// A host for driving the macro in tests: a real world model the test fills in, actions
    /// that are recorded instead of sent, and time that moves only when the test ticks it.
    /// </summary>
    internal sealed class MacroTestHost : IHost
    {
        public const uint PlayerId = 0x50000001;

        public MacroTestHost(bool actionsAvailable = true)
        {
            Actions = new RecordingActions { IsAvailable = actionsAvailable };
            WorldState.SetServerName("Test Server");
            WorldObject me = WorldState.GetOrAdd(PlayerId, out _);
            me.Name = "Tester";
            WorldState.SetPlayerId(PlayerId);
        }

        public WorldState WorldState { get; } = new WorldState(() => DateTimeOffset.UnixEpoch);

        public RecordingActions Actions { get; }

        public ListLog Log { get; } = new ListLog();

        /// <summary>Every line shown in the game's chat, as the player would read it.</summary>
        public List<string> ShownInGame { get; } = new List<string>();

        /// <summary>Every line shown in the game's chat, with the chat type - the colour - it was shown in.</summary>
        public List<(string Text, int ChatType)> Shown { get; } = new List<(string Text, int ChatType)>();

        IWorldView IHost.World => WorldState;

        ICharacterView IHost.Character => WorldState.Character;

        IGameActions IHost.Actions => Actions;

        /// <summary>Keys held, recorded; always available unless a test says otherwise.</summary>
        public GameInput Input { get; } = new GameInput(new ListLog()) { Attached = () => true, Publish = _ => { } };

        IGameInput IHost.Input => Input;

        public IGameData GameData => WorldState.GameData;

        IPluginLog IHost.Log => Log;

        /// <summary>Settings keyed "Plugin:Key", as the command line gives them; none unless a test adds some.</summary>
        public Dictionary<string, string> Settings { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        IReadOnlyDictionary<string, string> IHost.Settings => Settings;

        public string GetSetting(IPlugin plugin, string key) => Settings.TryGetValue(plugin.Name + ":" + key, out string value) ? value : null;

        public string GetDataDirectory(IPlugin plugin)
        {
            string path = Path.Combine(Path.GetTempPath(), "macro-test-" + plugin.Name);
            Directory.CreateDirectory(path);
            return path;
        }

        public PluginSettings<T> LoadSettings<T>(IPlugin plugin) where T : class, new()
            => new PluginSettings<T>(Path.Combine(GetDataDirectory(plugin), Guid.NewGuid().ToString("N") + ".json"), Log);

        public void RunOnGameThread(Action action) => action();

        /// <summary>One of the host's own lines, prefixed and typed as the real host shows them.</summary>
        public bool ShowInGame(string text)
            => !string.IsNullOrEmpty(text) && ShowInGame(GameHost.OwnPrefix + text, GameHost.OwnChatType);

        public bool ShowInGame(string text, int chatType)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            ShownInGame.Add(text);
            Shown.Add((text, chatType));
            return true;
        }

        // ------------------------------------------------------------------- building the world

        /// <summary>Puts the character somewhere, facing a heading in degrees clockwise from north.</summary>
        public void PlaceCharacter(uint landblockCell, float x, float y, float z = 0, double heading = 0)
        {
            WorldState.SetClientPosition(At(landblockCell, x, y, z, heading), default);
        }

        /// <summary>A location facing a heading, with the rotation the game would give it.</summary>
        public static Location At(uint landblockCell, float x, float y, float z = 0, double heading = 0)
        {
            // Heading h is a rotation of -h about z; a quaternion of angle a has w = cos(a/2), z = sin(a/2).
            double half = -heading * Math.PI / 180.0 / 2.0;
            return new Location(landblockCell, x, y, z, (float)Math.Cos(half), 0, 0, (float)Math.Sin(half));
        }

        /// <summary>Adds an object to the world and announces it, as a decoder would.</summary>
        public WorldObject AddObject(uint id, string name, Action<WorldObject> fill = null)
        {
            WorldObject obj = WorldState.GetOrAdd(id, out _);
            obj.Name = name;
            fill?.Invoke(obj);
            WorldState.NotifyCreated(obj);
            return obj;
        }

        /// <summary>Adds a creature standing at a place.</summary>
        public WorldObject AddCreature(uint id, string name, uint landblockCell, float x, float y, float z = 0)
            => AddObject(id, name, o =>
            {
                o.ItemType = 0x10;
                o.Location = At(landblockCell, x, y, z);
            });

        public void SetVital(uint vitalId, uint current)
            => WorldState.Character.GetOrAddVital(vitalId).Current = current;

        public void FinishUse(uint error = 0) => WorldState.NotifyUseFinished(error);

        /// <summary>The server turning down a move of an item, as InventoryServerSaveFailed does.</summary>
        public void RefuseMove(uint itemId, uint error = 0) => WorldState.NotifyMoveRefused(new MoveRefusal(itemId, error));

        // ------------------------------------------------------------------- events

        public event EventHandler<TimeSpan> Tick;

        public void RaiseTick(TimeSpan elapsed) => Tick?.Invoke(this, elapsed);

        public event EventHandler<string> ServerConnected { add => WorldState.ServerConnected += value; remove => WorldState.ServerConnected -= value; }

        public event EventHandler<uint> PlayerIdentified { add => WorldState.PlayerIdentified += value; remove => WorldState.PlayerIdentified -= value; }

        public event EventHandler<string> LoggedOff { add => WorldState.LoggedOff += value; remove => WorldState.LoggedOff -= value; }

        public event EventHandler<WorldObject> ObjectCreated { add => WorldState.ObjectCreated += value; remove => WorldState.ObjectCreated -= value; }

        public event EventHandler<WorldObject> ObjectUpdated { add => WorldState.ObjectUpdated += value; remove => WorldState.ObjectUpdated -= value; }

        public event EventHandler<WorldObject> ObjectAppraised { add => WorldState.ObjectAppraised += value; remove => WorldState.ObjectAppraised -= value; }

        public event EventHandler<WorldObject> ObjectMoved { add => WorldState.ObjectMoved += value; remove => WorldState.ObjectMoved -= value; }

        public event EventHandler<uint> ObjectRemoved { add => WorldState.ObjectRemoved += value; remove => WorldState.ObjectRemoved -= value; }

        public event EventHandler<ContainerContents> ContainerViewed { add => WorldState.ContainerViewed += value; remove => WorldState.ContainerViewed -= value; }

        public event EventHandler<ChatMessage> ChatReceived { add => WorldState.ChatReceived += value; remove => WorldState.ChatReceived -= value; }

        public event EventHandler<uint> UseFinished { add => WorldState.UseFinished += value; remove => WorldState.UseFinished -= value; }

        public event EventHandler<uint> ContainerClosed { add => WorldState.ContainerClosed += value; remove => WorldState.ContainerClosed -= value; }

        public event EventHandler<MoveRefusal> MoveRefused { add => WorldState.MoveRefused += value; remove => WorldState.MoveRefused -= value; }

        public event EventHandler<DamageTaken> DamageTaken { add => WorldState.DamageTaken += value; remove => WorldState.DamageTaken -= value; }

        public event EventHandler<string> AttackEvaded { add => WorldState.AttackEvaded += value; remove => WorldState.AttackEvaded -= value; }

        public event EventHandler<Enchantment> EnchantmentChanged { add => WorldState.EnchantmentChanged += value; remove => WorldState.EnchantmentChanged -= value; }

        public event EventHandler<uint> EnchantmentRemoved { add => WorldState.EnchantmentRemoved += value; remove => WorldState.EnchantmentRemoved -= value; }

        public event EventHandler CharacterUpdated { add => WorldState.CharacterUpdated += value; remove => WorldState.CharacterUpdated -= value; }

        public event EventHandler<uint> AttackFinished { add => WorldState.AttackFinished += value; remove => WorldState.AttackFinished -= value; }

        public event EventHandler<DamageDealt> DamageDealt { add => WorldState.DamageDealt += value; remove => WorldState.DamageDealt -= value; }

        public event EventHandler<string> TargetEvaded { add => WorldState.TargetEvaded += value; remove => WorldState.TargetEvaded -= value; }

        public event EventHandler AttackCommenced { add => WorldState.AttackCommenced += value; remove => WorldState.AttackCommenced -= value; }

        public event EventHandler<bool> PortalSpaceChanged { add => WorldState.PortalSpaceChanged += value; remove => WorldState.PortalSpaceChanged -= value; }

        public event EventHandler<uint> VendorChanged { add => WorldState.VendorChanged += value; remove => WorldState.VendorChanged -= value; }

        public event EventHandler<string> Died { add => WorldState.Died += value; remove => WorldState.Died -= value; }

        /// <summary>Never raised: the macro's tests build the world directly, not from messages.</summary>
        public event EventHandler<AC.Host.Transport.GameMessageEventArgs> MessageSeen { add { } remove { } }

        /// <summary>Lines run as though typed, in order, with the plugin that ran each.</summary>
        public List<(string Text, IPlugin From)> ChatCommands { get; } = new List<(string, IPlugin)>();

        /// <summary>What a line run as though typed comes to; sent, unless a test says otherwise.</summary>
        public Func<string, ChatCommandOutcome> ChatCommandOutcome { get; set; } = _ => AC.Host.Plugins.ChatCommandOutcome.Sent;

        public ChatCommandOutcome RunChatCommand(string text, IPlugin from)
        {
            ChatCommands.Add((text, from));
            return ChatCommandOutcome(text);
        }
    }

    /// <summary>Actions written down instead of sent, in order, as "verb args".</summary>
    internal sealed class RecordingActions : IGameActions
    {
        public bool IsAvailable { get; set; } = true;

        public List<string> Calls { get; } = new List<string>();

        private Task<bool> Record(string call)
        {
            if (!IsAvailable)
                return Task.FromResult(false);
            Calls.Add(call);
            return Task.FromResult(true);
        }

        public Task<bool> AppraiseAsync(uint objectId) => Record($"appraise 0x{objectId:X8}");

        public Task<bool> UseAsync(uint objectId) => Record($"use 0x{objectId:X8}");

        public Task<bool> UseOnAsync(uint sourceId, uint targetId) => Record($"useon 0x{sourceId:X8} 0x{targetId:X8}");

        public Task<bool> MoveToContainerAsync(uint objectId, uint containerId, int slot = 0) => Record($"move 0x{objectId:X8} 0x{containerId:X8} {slot}");

        public Task<bool> DropAsync(uint objectId) => Record($"drop 0x{objectId:X8}");

        public Task<bool> SayAsync(string text) => Record($"say {text}");

        public Task<bool> MoveAsync(ClientMotionState motion) => Record($"motion {motion}");

        public Task<bool> WalkForwardAsync(float speed = 1.0f) => Record($"walk {speed:0.##}");

        public Task<bool> TurnAsync(float speed = 1.0f) => Record($"turn {speed:0.##}");

        public Task<bool> StopAsync() => Record("stop");

        public Task<bool> CastAsync(uint targetId, uint spellId) => Record($"cast {spellId} 0x{targetId:X8}");

        public Task<bool> SetCombatModeAsync(CombatMode mode) => Record($"stance {mode}");

        public Task<bool> QueryHealthAsync(uint objectId) => Record($"health 0x{objectId:X8}");

        public Task<bool> MeleeAttackAsync(uint targetId, AttackHeight height, float power) => Record($"melee 0x{targetId:X8} {height} {power:0.##}");

        public Task<bool> MissileAttackAsync(uint targetId, AttackHeight height, float accuracy) => Record($"missile 0x{targetId:X8} {height} {accuracy:0.##}");

        public Task<bool> CancelAttackAsync() => Record("cancel attack");

        public Task<bool> CastUntargetedAsync(uint spellId) => Record($"cast {spellId}");

        public Task<bool> WieldAsync(uint objectId, uint slot) => Record($"wield 0x{objectId:X8} 0x{slot:X}");

        public Task<bool> StackableMergeAsync(uint fromStackId, uint toStackId, int amount) => Record($"merge 0x{fromStackId:X8} 0x{toStackId:X8} {amount}");

        public Task<bool> SalvageAsync(uint toolId, IReadOnlyList<uint> itemIds) => Record($"salvage 0x{toolId:X8} {string.Join(" ", itemIds.Select(id => $"0x{id:X8}"))}");
    }
}
