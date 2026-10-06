using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AC.Host.World;

namespace AC.Host.Plugins
{
    /// <summary>
    /// A plugin. The host constructs one instance per plugin type it finds, calls
    /// <see cref="Startup"/> with the services it may use, and <see cref="Shutdown"/>
    /// when the host stops or the plugin is unloaded.
    /// </summary>
    /// <remarks>
    /// Every callback from the host - these two methods and every event on
    /// <see cref="IHost"/> - arrives on the host's single game thread, in order, so a
    /// plugin sees a consistent world and needs no locking of its own. The cost is
    /// that a slow handler stalls every other plugin: do long work elsewhere and
    /// come back via <see cref="IHost.RunOnGameThread"/>.
    /// </remarks>
    public interface IPlugin
    {
        /// <summary>Short unique name; also names the plugin's data directory and settings.</summary>
        string Name { get; }

        void Startup(IHost host);

        void Shutdown();
    }

    /// <summary>
    /// What a plugin gets from the host.
    /// </summary>
    /// <remarks>
    /// This contract is deliberately independent of how the host reaches the game.
    /// Today that is a UDP relay decoding the wire protocol; if a client ever offers
    /// a native API, the host swaps the transport and plugins are none the wiser.
    /// </remarks>
    public interface IHost
    {
        /// <summary>Everything the host currently knows about the world.</summary>
        IWorldView World { get; }

        /// <summary>The player character, as far as it has been described.</summary>
        ICharacterView Character { get; }

        /// <summary>
        /// Things a plugin can make the character do. Whether any of them is possible
        /// right now is reported by <see cref="IGameActions.IsAvailable"/>, which follows the
        /// player's "Let plugins act" switch and so can change during a session; while it is
        /// false every call reports failure rather than throwing.
        /// </summary>
        IGameActions Actions { get; }

        /// <summary>
        /// The game's movement keys, held down as a player holds them. How a plugin walks:
        /// the client decides where its character is, so moving means pressing its keys.
        /// Available while the overlay is attached and the player lets plugins act.
        /// </summary>
        IGameInput Input { get; }

        /// <summary>
        /// What the game's window is doing - minimized, drawing, parked off-screen in place of
        /// minimized - as the overlay inside the game last said; <see cref="GameWindowState.Unknown"/>
        /// without one. Everything done over the network goes on whatever the window does; only
        /// <see cref="Input"/> depends on the game taking keys, which a minimized game may not.
        /// </summary>
        GameWindowState GameWindow { get; }

        /// <summary>
        /// AC:Unreal's own client plugins as the client's settings say the player has switched them
        /// on: which are enabled, with which permissions - its Unattended Combat Manager, a Virindi
        /// Tank of the client's own, among them (<see cref="ClientPluginsState.UcmEnabled"/>).
        /// <see cref="ClientPluginsState.Unknown"/> while there is no client whose settings can be
        /// read. Enabled is all that can be seen: a client plugin acts only once the player presses
        /// its Start, and whether it is running the client writes nowhere.
        /// </summary>
        ClientPluginsState ClientPlugins => ClientPluginsState.Unknown;

        /// <summary>
        /// The client's own data files, where ids become names and colours. Check
        /// <see cref="IGameData.IsAvailable"/>: a host started without them answers
        /// nothing rather than guessing.
        /// </summary>
        IGameData GameData { get; }

        IPluginLog Log { get; }

        /// <summary>
        /// Settings handed to the host at startup, keyed "Plugin:Key". A plugin reads
        /// its own with <see cref="GetSetting"/>.
        /// </summary>
        IReadOnlyDictionary<string, string> Settings { get; }

        /// <summary>Value of "&lt;PluginName&gt;:&lt;key&gt;", or null.</summary>
        string GetSetting(IPlugin plugin, string key);

        /// <summary>A directory this plugin may keep files in. Created on first use.</summary>
        string GetDataDirectory(IPlugin plugin);

        /// <summary>
        /// The plugin's typed settings, read from <c>settings.json</c> in its data
        /// directory, or defaults if there is no file yet.
        /// </summary>
        /// <remarks>
        /// One settings type per plugin. Change <c>Value</c> and call <c>Save</c>; the
        /// host saves again at shutdown regardless, so nothing changed is lost to a
        /// plugin that forgot. The command-line <c>--set</c> mechanism is separate and
        /// unchanged: it is for a one-off override, this is for what persists.
        /// </remarks>
        PluginSettings<T> LoadSettings<T>(IPlugin plugin) where T : class, new();

        /// <summary>Runs <paramref name="action"/> on the game thread, after anything already queued.</summary>
        void RunOnGameThread(Action action);

        /// <summary>
        /// Puts one of the host's own lines in the game's chat window, prefixed "[Decal] ".
        /// </summary>
        /// <remarks>
        /// The client cannot be drawn into, so this is the only way anything appears
        /// inside the game. The line arrives as though the server had sent it, which it
        /// did not - so it is prefixed rather than disguised, and nothing should use this
        /// to imitate the server or another player. Returns false if the host cannot
        /// reach the client, which is the case over a capture. Showing a line is not acting,
        /// so it works while the player has acting switched off.
        /// <para>
        /// The prefix is the host's, which stands where Decal did. A plugin with words of its
        /// own - Virindi Tank's "[VTank]" lines, a Decal plugin's "[VGI]" - shows them with
        /// <see cref="ShowInGame(string, int)"/>, under its own prefix and in its own colour.
        /// </para>
        /// </remarks>
        bool ShowInGame(string text);

        /// <summary>
        /// Puts a line in the game's chat window exactly as given, in the client's chat type
        /// <paramref name="chatType"/>.
        /// </summary>
        /// <remarks>
        /// The chat type is what Decal's AddChatText called a colour, and the same numbers as
        /// the server's own chat types - 0 broadcast, 2 speech, 3 a tell, 5 system, 6 combat,
        /// 7 magic and so on: the client draws the line in its type's colour, in the chat
        /// windows the player set that type to appear in. Nothing is added to the text, so the
        /// caller says whose line it is, as plugins did under Decal - "[VTank] ..." - and the
        /// same rule holds as for <see cref="ShowInGame(string)"/>: never the server's or
        /// another player's words. False when the host cannot reach the client, or for an
        /// empty line. A line too long for one message - several hundred characters - arrives
        /// as several, broken at spaces.
        /// </remarks>
        bool ShowInGame(string text, int chatType);

        /// <summary>Raised roughly every 100 ms with the time since the previous tick.</summary>
        event EventHandler<TimeSpan> Tick;

        /// <summary>The server announced itself; the session is up.</summary>
        event EventHandler<string> ServerConnected;

        /// <summary>The player's object id is now known.</summary>
        event EventHandler<uint> PlayerIdentified;

        /// <summary>
        /// The character has left the world - logged off, sent back to the character list, booted,
        /// or its session gone - with why. Raised while <see cref="Character"/> still describes it,
        /// so a plugin can note what it keeps for it; the character and the world are emptied
        /// straight after, and the next <see cref="PlayerIdentified"/> is a new login, even of the
        /// same character. Decal's Logoff.
        /// </summary>
        event EventHandler<string> LoggedOff;

        /// <summary>
        /// The character has been asked to leave the world - the player chose Log Out, or a plugin
        /// did (<see cref="IGameActions.LogOutAsync"/>) - and the server has yet to agree; once a
        /// logoff, while <see cref="Character"/> still describes it. <see cref="LoggedOff"/> follows
        /// when the server has taken it out. Decal's Logoff with LogoffEventType.Requested, on which
        /// Virindi Tank stopped its macro.
        /// </summary>
        event EventHandler LoggingOff;

        /// <summary>An object entered the host's view, or was re-sent in full.</summary>
        event EventHandler<WorldObject> ObjectCreated;

        /// <summary>One or more fields of a known object changed.</summary>
        event EventHandler<WorldObject> ObjectUpdated;

        /// <summary>Appraisal data for an object arrived.</summary>
        event EventHandler<WorldObject> ObjectAppraised;

        /// <summary>An object started, changed or stopped a motion.</summary>
        event EventHandler<WorldObject> ObjectMoved;

        /// <summary>An object left the host's view. Only the id remains.</summary>
        event EventHandler<uint> ObjectRemoved;

        /// <summary>The server listed a container's contents.</summary>
        event EventHandler<ContainerContents> ContainerViewed;

        event EventHandler<ChatMessage> ChatReceived;

        /// <summary>
        /// An action has finished. The value is the server's error, or zero for success.
        /// This is the only reliable signal that it is safe to send the next action:
        /// the server processes one at a time and silently drops the rest.
        /// </summary>
        event EventHandler<uint> UseFinished;

        /// <summary>
        /// A container on the ground has closed, so whatever was in it is out of reach.
        /// </summary>
        event EventHandler<uint> ContainerClosed;

        /// <summary>
        /// The server refused to move an item - a pick-up from a corpse, a move between packs, a
        /// drop - and it stayed where it was: InventoryServerSaveFailed. Carries the item and the
        /// server's error, which is often zero; a full pack or too much to carry is said in chat.
        /// A move's other answer is the item arriving (<see cref="ObjectUpdated"/>), never
        /// <see cref="UseFinished"/>.
        /// </summary>
        event EventHandler<AC.Host.World.MoveRefusal> MoveRefused;

        /// <summary>Something landed a blow on the character.</summary>
        event EventHandler<AC.Host.World.DamageTaken> DamageTaken;

        /// <summary>The character evaded an attack. Carries the attacker's name.</summary>
        event EventHandler<string> AttackEvaded;

        /// <summary>An enchantment on the character was added or refreshed.</summary>
        event EventHandler<AC.Host.World.Enchantment> EnchantmentChanged;

        /// <summary>An enchantment went, named by spell id and layer packed into one word.</summary>
        event EventHandler<uint> EnchantmentRemoved;

        /// <summary>The character's attributes, skills or vitals changed.</summary>
        event EventHandler CharacterUpdated;

        /// <summary>
        /// An attack the character made has finished: one swing or shot of an attack the
        /// server is repeating (zero), or the whole attack (an error, ActionCancelled when it
        /// simply ended). Attacks are answered by this, never by <see cref="UseFinished"/>.
        /// </summary>
        event EventHandler<uint> AttackFinished;

        /// <summary>A blow the character struck landed.</summary>
        event EventHandler<AC.Host.World.DamageDealt> DamageDealt;

        /// <summary>Something the character attacked evaded the blow. Carries its name.</summary>
        event EventHandler<string> TargetEvaded;

        /// <summary>The next swing or shot of an attack the server is repeating has begun.</summary>
        event EventHandler AttackCommenced;

        /// <summary>
        /// Runs a line as though the player had typed it into the game's chat box - what
        /// Decal's InvokeChatParser did. It is offered first to every other plugin that takes
        /// commands (<see cref="IChatCommands"/>), the one asking excepted; then, if it is one
        /// of the game client's own commands the server carries out - "/f", "/a", "/t", "/ls",
        /// "/mp", an "@" command, plain speech - the action the client would have sent is sent,
        /// which needs acting to be allowed. What happened is the answer: a command that only
        /// changes the client's own windows cannot be done from outside it, and says so.
        /// Game thread only.
        /// </summary>
        ChatCommandOutcome RunChatCommand(string text, IPlugin from);

        /// <summary>
        /// Puts a row on the status HUD another plugin shows (<see cref="IStatusRows"/>) - Virindi
        /// HUDs' Status HUD - or changes its value: what Virindi HUDs' StatusModel.UpdateEntry did
        /// for every Decal plugin. Nothing happens where no plugin shows one. Game thread only. Has
        /// a default, which shows nothing, so another IHost needs nothing.
        /// </summary>
        /// <param name="colour">The row's colour as 0xAARRGGBB; white unless the plugin names one.</param>
        void UpdateStatusRow(string plugin, string entry, string value, long colour = 0xFFFFFFFF)
        {
        }

        /// <summary>
        /// The character went into portal space (true) - the server teleported it - or came
        /// out (false), the client having arrived. Decal's ChangePortalMode.
        /// </summary>
        event EventHandler<bool> PortalSpaceChanged;

        /// <summary>A vendor's window opened, with the vendor's id, or closed, with 0. See <see cref="ICharacterView.OpenVendorId"/>.</summary>
        event EventHandler<uint> VendorChanged;

        /// <summary>The character died. Carries the server's message to it, as the chat window shows it.</summary>
        event EventHandler<string> Died;

        /// <summary>
        /// Every whole game message, either way - the server's to the client and the client's to
        /// the server - as its opcode and bytes, raised once the host has applied it to the world.
        /// </summary>
        /// <remarks>
        /// For a plugin that has to read what the host does not decode: the Decal compatibility
        /// layer gives these to Decal plugins as ServerDispatch and ClientDispatch, parsed against
        /// Decal's messages.xml. Any other plugin wants the events above, which say what happened;
        /// these are only the bytes, the same ones the client and the server saw. Raised for
        /// every message, so a handler that does anything slow slows everything.
        /// <para>
        /// A session carried on from the host before - one restarted while the game stays
        /// connected - raises it again for the messages the world was built from, in their order,
        /// once the login's other events have been raised and before the first message relayed:
        /// what a login brings, and what changed it since, but not chat, combat's notices or
        /// anything else that only told of a moment, nor what the player did besides entering
        /// the world. A plugin that built its picture of the world from these at the login builds
        /// the same one again.
        /// </para>
        /// </remarks>
        event EventHandler<AC.Host.Transport.GameMessageEventArgs> MessageSeen;
    }

    public interface IPluginLog
    {
        void Info(string message);

        void Warn(string message);

        void Error(string message, Exception exception = null);
    }

    /// <summary>
    /// Actions the character can be asked to perform. Every method reports whether
    /// the request was sent, never whether it succeeded in the game - that shows up
    /// later as world changes.
    /// </summary>
    public interface IGameActions
    {
        /// <summary>False until the host can inject client messages.</summary>
        bool IsAvailable { get; }

        Task<bool> AppraiseAsync(uint objectId);

        Task<bool> UseAsync(uint objectId);

        Task<bool> UseOnAsync(uint sourceId, uint targetId);

        Task<bool> MoveToContainerAsync(uint objectId, uint containerId, int slot = 0);

        Task<bool> DropAsync(uint objectId);

        Task<bool> SayAsync(string text);

        /// <summary>
        /// Sets what the character's body is doing. Movement is a state that persists
        /// until replaced, so a walk continues until <see cref="StopAsync"/>.
        /// </summary>
        Task<bool> MoveAsync(AC.Host.Decoding.ClientMotionState motion);

        Task<bool> WalkForwardAsync(float speed = 1.0f);

        /// <summary>Turns in place. Positive is right, negative is left.</summary>
        Task<bool> TurnAsync(float speed = 1.0f);

        Task<bool> StopAsync();

        /// <summary>Casts a spell at something. A self-buff targets the character itself.</summary>
        Task<bool> CastAsync(uint targetId, uint spellId);

        /// <summary>Changes stance. Casting needs Magic, swinging needs Melee.</summary>
        Task<bool> SetCombatModeAsync(AC.Host.Decoding.CombatMode mode);

        /// <summary>Asks for a creature's health, which comes back as a fraction.</summary>
        Task<bool> QueryHealthAsync(uint objectId);

        /// <summary>
        /// Swings at a creature, with a power from 0 to 1. Needs Melee stance. The server
        /// walks the character to a target out of reach, and repeats the attack by itself if
        /// the character's options say to; each swing ends with <see cref="IHost.AttackFinished"/>.
        /// </summary>
        Task<bool> MeleeAttackAsync(uint targetId, AC.Host.Decoding.AttackHeight height, float power);

        /// <summary>
        /// Shoots at a creature, with an accuracy from 0 to 1. Needs Missile stance and
        /// ammunition; the server turns the character to face the target but does not move it.
        /// </summary>
        Task<bool> MissileAttackAsync(uint targetId, AC.Host.Decoding.AttackHeight height, float accuracy);

        /// <summary>Stops the attack under way, a repeating one included.</summary>
        Task<bool> CancelAttackAsync();

        /// <summary>Casts a spell that takes no target.</summary>
        Task<bool> CastUntargetedAsync(uint spellId);

        /// <summary>
        /// Wields an item the character carries, in a slot from <see cref="AC.Host.Decoding.EquipMasks"/>.
        /// The slot must be free: the server refuses to wield over something already there.
        /// </summary>
        Task<bool> WieldAsync(uint objectId, uint slot);

        /// <summary>
        /// Moves <paramref name="amount"/> from one stack onto another of the same kind, as
        /// dragging one stack onto another does. The server refuses an amount the target
        /// cannot hold, so it is the caller's to work out. Within the character's own packs
        /// the answer is the stacks' new sizes (and the source's removal when it empties),
        /// not UseDone.
        /// </summary>
        Task<bool> StackableMergeAsync(uint fromStackId, uint toStackId, int amount);

        /// <summary>
        /// Salvages items with an Ultimate Salvaging Tool, as the salvage panel's Salvage button
        /// does: the items are destroyed and their salvage added to bags in the packs. Salvage
        /// bags given together are combined. The answer is the items' removal and the bags'
        /// arrival, not UseDone.
        /// </summary>
        Task<bool> SalvageAsync(uint toolId, IReadOnlyList<uint> itemIds);

        /// <summary>
        /// Hands <paramref name="amount"/> of an item the character carries to a player or an
        /// NPC, as dropping it on them does. The server walks the character there first; what
        /// comes of it is the item leaving the packs, or the NPC's refusal in the chat.
        /// </summary>
        Task<bool> GiveAsync(uint objectId, uint targetId, int amount);

        /// <summary>
        /// Logs the character out to the character list, as the game's own Log Out does - Decal's
        /// Hooks.Logout. The account stays connected: nothing about the password is involved, and
        /// <see cref="EnterWorldAsync"/> brings a character back. Only from the world
        /// (<see cref="AC.Host.World.IWorldView.Phase"/>). The server takes a few seconds, the
        /// character playing its logging-out motion; plugins then hear what a player's own logoff
        /// tells them - <see cref="IHost.LoggingOff"/> as it is asked for, <see cref="IHost.LoggedOff"/>
        /// once it is done, and the account's characters listed again
        /// (<see cref="AC.Host.World.IWorldView.AccountCharacters"/>).
        /// </summary>
        Task<bool> LogOutAsync();

        /// <summary>
        /// Enters the world as one of the account's characters
        /// (<see cref="AC.Host.World.IWorldView.AccountCharacters"/>), from the character list - as
        /// choosing it there and clicking Enter does, which is what the overlay does in AC:Unreal's
        /// character select, so this needs the overlay attached and the game not minimized. No
        /// password is involved, which is why this works only while the account is still connected
        /// at the character list, never after the session has ended. Plugins then hear a login:
        /// <see cref="IHost.PlayerIdentified"/>, every object, and the rest.
        /// </summary>
        Task<bool> EnterWorldAsync(uint characterId);

        /// <summary>
        /// Asks a player to join the character's fellowship, as the fellowship panel's Recruit
        /// does - Decal's Hooks.FellowshipRecruit. The server says in chat what came of it, and a
        /// member joining is the fellowship's next update (<see cref="ICharacterView.Fellowship"/>).
        /// False where actions cannot be sent, as for the rest; an implementation that has no
        /// fellowship actions says false.
        /// </summary>
        Task<bool> FellowshipRecruitAsync(uint playerId) => Task.FromResult(false);

        /// <summary>
        /// Leaves the fellowship, or - <paramref name="disband"/>, as its leader - ends it for
        /// everyone: the panel's Quit and Disband, Decal's FellowshipQuit and FellowshipDisband.
        /// </summary>
        Task<bool> FellowshipQuitAsync(bool disband) => Task.FromResult(false);

        /// <summary>Dismisses a member, as the fellowship's leader: Decal's FellowshipDismiss.</summary>
        Task<bool> FellowshipDismissAsync(uint playerId) => Task.FromResult(false);

        /// <summary>Makes another member the leader, as the leader: Decal's FellowshipGrantLeader.</summary>
        Task<bool> FellowshipAssignLeaderAsync(uint playerId) => Task.FromResult(false);

        /// <summary>
        /// Lets every member recruit (<paramref name="open"/>), or only the leader, as the leader:
        /// Decal's FellowshipSetOpen.
        /// </summary>
        Task<bool> FellowshipSetOpenAsync(bool open) => Task.FromResult(false);
    }
}
