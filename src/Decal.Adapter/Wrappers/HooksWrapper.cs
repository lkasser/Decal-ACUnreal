using System;
using System.Drawing;
using System.Threading.Tasks;
using AC.Host.Decoding;
using AC.Host.Plugins;
using AC.Host.World;
using Decal.Adapter.Hosting;

namespace Decal.Adapter.Wrappers
{
    /// <summary>
    /// Decal's hooks into the client: what the character is doing, and the actions a plugin
    /// can make it take.
    /// </summary>
    /// <remarks>
    /// Actions go to the host's <see cref="IGameActions"/>, which sends them only while the
    /// player lets plugins act, and reports - never throws - when it will not. Decal's hooks
    /// returned nothing, so neither do these: what an action did shows up later as world
    /// changes, as it always did. A turn to a heading is the game's own turn keys, held through
    /// the overlay (<see cref="Facing"/>). Hooks with no counterpart in the protocol - the
    /// client's own selection, its window layout, its trade and vendor panels - do nothing, and
    /// say so once in the log; a plugin's own selection still stands for what it then uses on it.
    /// </remarks>
    public sealed class HooksWrapper : MarshalByRefObject, IDisposable
    {
        private readonly DecalRuntime _runtime;

        internal HooksWrapper(DecalRuntime runtime)
        {
            _runtime = runtime;
            Attribute = new HookIndexer<AttributeType>(AttributeValue);
            AttributeClicks = new HookIndexer<AttributeType>(t => Character.Attributes.TryGetValue(AttributeId(t), out AttributeState a) ? unchecked((int)a.Ranks) : 0);
            AttributeStart = new HookIndexer<AttributeType>(t => Character.Attributes.TryGetValue(AttributeId(t), out AttributeState a) ? unchecked((int)a.StartingValue) : 0);
            AttributeTotalXP = new HookIndexer<AttributeType>(t => Character.Attributes.TryGetValue(AttributeId(t), out AttributeState a) ? unchecked((int)a.ExperienceSpent) : 0);
            Skill = new HookIndexer<SkillType>(t => Character.GetSkillBase(SkillId(t)));
            SkillClicks = new HookIndexer<SkillType>(t => Character.Skills.TryGetValue(SkillId(t), out SkillState s) ? s.Ranks : 0);
            SkillFreePoints = new HookIndexer<SkillType>(t => 0);
            SkillTotalXP = new HookIndexer<SkillType>(t => Character.Skills.TryGetValue(SkillId(t), out SkillState s) ? unchecked((int)s.ExperienceSpent) : 0);
            SkillTrainLevel = new HookIndexer<SkillType>(t => Character.Skills.TryGetValue(SkillId(t), out SkillState s) ? unchecked((int)s.AdvancementClass) : 0);
            Vital = new HookIndexer<VitalType>(VitalValue);
            VitalClicks = new HookIndexer<VitalType>(t => Character.Vitals.TryGetValue(VitalId(t), out VitalState v) ? unchecked((int)v.Ranks) : 0);
            VitalTotalXP = new HookIndexer<VitalType>(t => Character.Vitals.TryGetValue(VitalId(t), out VitalState v) ? unchecked((int)v.ExperienceSpent) : 0);
            Misc = new HookIndexer<int>(t => 0);
        }

        private ICharacterView Character => _runtime.Host.Character;

        private IGameActions Game => _runtime.Host.Actions;

        // ------------------------------------------------------------------- the character

        public int Landcell => Character.Location is Location l ? unchecked((int)l.LandblockCell) : 0;

        public double LocationX => Character.Location?.X ?? 0;

        public double LocationY => Character.Location?.Y ?? 0;

        public double LocationZ => Character.Location?.Z ?? 0;

        /// <summary>Degrees clockwise from north. Setting it turns the character, as <see cref="FaceHeading"/> does.</summary>
        public double Heading
        {
            get => Character.Location is Location l ? WorldObject.HeadingOf(l) : 0;
            set => FaceHeading(value, true);
        }

        public double HeadingRadians
        {
            get => Heading * Math.PI / 180.0;
            set => FaceHeading(value * 180.0 / Math.PI, true);
        }

        /// <summary>
        /// The object selected: the one a plugin last chose (<see cref="SelectItem"/>) until the
        /// player selects something in the client, and the client's own after that.
        /// </summary>
        public int CurrentSelection
        {
            get => unchecked((int)Selected);
            set => SelectItem(value);
        }

        /// <summary>
        /// The plugin's choice while it stands - the client's selection has not moved since, and
        /// the object is still in the world - else the client's own.
        /// </summary>
        private uint Selected
            => _chosen != 0 && Character.SelectedId == _clientWhenChosen && _runtime.Host.World.TryGet(_chosen, out _)
                ? _chosen
                : Character.SelectedId;

        private uint _chosen;
        private uint _clientWhenChosen;

        public int PreviousSelection { get; set; }

        public int SelectedStackCount { get; set; }

        public int MaxSelectedStackCount => 0;

        /// <summary>Whether the chat bar has the keyboard. The host cannot tell, and says no.</summary>
        public bool ChatState => false;

        /// <summary>The character's stance. The host does not track it, so peace.</summary>
        public CombatState CombatMode => CombatState.Peace;

        public int BusyState => 0;

        public int BusyStateId => 0;

        public int PointerState => 0;

        public int CommandInterpreter => 0;

        /// <summary>The container on the ground whose contents the server last listed, until it closes; 0 for none.</summary>
        public int OpenedContainer => unchecked((int)_runtime.OpenedContainer);

        public int VendorId => 0;

        /// <summary>
        /// The client's window. The host cannot see it, so this is a nominal 1024 by 768 - enough
        /// for a plugin that lays a hud out against it to put it somewhere sensible.
        /// </summary>
        public Rectangle RegionWindow => new Rectangle(0, 0, 1024, 768);

        /// <summary>The 3D area of the client's window. Nominal, as <see cref="RegionWindow"/> is.</summary>
        public Rectangle Region3D => new Rectangle(0, 0, 1024, 768);

        public HookIndexer<AttributeType> Attribute { get; }

        public HookIndexer<AttributeType> AttributeClicks { get; }

        public HookIndexer<AttributeType> AttributeStart { get; }

        public HookIndexer<AttributeType> AttributeTotalXP { get; }

        public HookIndexer<SkillType> Skill { get; }

        public HookIndexer<SkillType> SkillClicks { get; }

        public HookIndexer<SkillType> SkillFreePoints { get; }

        public HookIndexer<SkillType> SkillTotalXP { get; }

        public HookIndexer<SkillType> SkillTrainLevel { get; }

        public HookIndexer<VitalType> Vital { get; }

        public HookIndexer<VitalType> VitalClicks { get; }

        public HookIndexer<VitalType> VitalTotalXP { get; }

        public HookIndexer<int> Misc { get; }

        // ------------------------------------------------------------------- chat

        /// <summary>
        /// Shows a line in the chat window as written, in the chat type <paramref name="color"/>
        /// names. See <see cref="DecalRuntime.ShowChat"/> for where it goes.
        /// </summary>
        public void AddChatText(string text, int color) => _runtime.ShowChat(text, color);

        /// <summary>
        /// As <see cref="AddChatText(string, int)"/>. Decal's <paramref name="target"/> window
        /// cannot be named in what reaches the client, so the line goes where its chat type does.
        /// </summary>
        public void AddChatText(string text, int color, int target) => _runtime.ShowChat(text, color);

        /// <summary>
        /// As <see cref="AddChatText(string, int)"/>. Decal's raw form left the closing newline
        /// to the plugin; here every call is a line of its own either way.
        /// </summary>
        public void AddChatTextRaw(string text, int color) => _runtime.ShowChat(text, color);

        /// <summary>As <see cref="AddChatText(string, int, int)"/>, the window ignored the same way.</summary>
        public void AddChatTextRaw(string text, int color, int target) => _runtime.ShowChat(text, color);

        /// <summary>The status line above the chat window. The host has no such line, so it goes to the log.</summary>
        public void AddStatusText(string text) => _runtime.Log.Info("[Decal] " + text);

        /// <summary>
        /// Treats text as though the player had typed it: offered to plugins as a command
        /// first, then run as the game's chat box would - the host's other plugins' commands,
        /// the game client's own commands the server carries out ("/f", "/t", an "@" command),
        /// and plain speech (<see cref="IHost.RunChatCommand"/>). A command that changes only the
        /// client's own windows cannot be done from outside it, and says so once.
        /// </summary>
        /// <remarks>
        /// A plugin that has just offered the line to the plugins itself, through Decal.dll's
        /// DispatchOnChatCommand (<see cref="DecalNative"/>), and had it declined, is not made to
        /// offer it again: Mag-Tools and Virindi HUDs' chat window did that, then this.
        /// </remarks>
        public void InvokeChatParser(string text)
        {
            bool declined = _runtime.TakeDeclined(text);
            if (string.IsNullOrWhiteSpace(text) || (!declined && _runtime.InvokeCommandLine(text)))
                return;

            _runtime.RunGameCommand(text);
        }

        // ------------------------------------------------------------------- actions

        public void UseItem(int objectId, int useState) => UseItem(objectId, useState, 0);

        /// <summary>
        /// Uses an item. A use state of 1 means "on the current selection", which is how Decal
        /// applied a kit or key to whatever was selected - the plugin's own choice, when it has
        /// just made one (<see cref="SelectItem"/>): Mag-Tools' "/mt use key on chest" selects the
        /// chest, then uses the key so.
        /// </summary>
        public void UseItem(int objectId, int useState, int useMethod)
        {
            uint target = Selected;
            if (useState == 1 && target != 0)
                Send(Game.UseOnAsync(unchecked((uint)objectId), target));
            else
                Send(Game.UseAsync(unchecked((uint)objectId)));
        }

        public void ApplyItem(int useThis, int onThis) => Send(Game.UseOnAsync(unchecked((uint)useThis), unchecked((uint)onThis)));

        /// <summary>
        /// Casts a spell at an object, or - with no object - as the client cast one with nothing
        /// named: a spell that takes no target (a ring, a summoned portal) at nothing, any other at
        /// the character itself.
        /// </summary>
        public void CastSpell(int spellId, int objectId)
        {
            uint spell = unchecked((uint)spellId);
            if (objectId != 0)
            {
                Send(Game.CastAsync(unchecked((uint)objectId), spell));
                return;
            }

            AC.Dat.SpellInfo info = _runtime.Host.GameData?.IsAvailable == true ? _runtime.Host.GameData.GetSpell(spell) : null;
            if (info != null && info.TargetType == 0)
                Send(Game.CastUntargetedAsync(spell));
            else
                Send(Game.CastAsync(Character.Id, spell));
        }

        public void RequestId(int objectId) => Send(Game.AppraiseAsync(unchecked((uint)objectId)));

        public void DropItem(int objectId) => Send(Game.DropAsync(unchecked((uint)objectId)));

        public void MoveItem(int objectId, int packId, int slot, bool stack)
            => Send(Game.MoveToContainerAsync(unchecked((uint)objectId), unchecked((uint)packId), slot));

        public void MoveItem(int objectId, int destinationId) => MoveItem(objectId, destinationId, 0, true);

        public void MoveItem(int objectId, int destinationId, int moveFlags) => MoveItem(objectId, destinationId, 0, true);

        /// <summary>Hands an item to a player or NPC: the whole of a stack.</summary>
        public void GiveItem(int lObject, int lDestination)
        {
            uint item = unchecked((uint)lObject);
            int amount = _runtime.Host.World?.Get(item)?.StackSize ?? 1;
            Send(Game.GiveAsync(item, unchecked((uint)lDestination), amount));
        }

        public void SetCombatMode(CombatState newMode) => Send(Game.SetCombatModeAsync((CombatMode)(uint)newMode));

        /// <summary>
        /// Runs forward, or stops. The only movement Decal's hooks offered that the host's
        /// keys can do.
        /// </summary>
        public void SetAutorun(bool on) => Send(on ? Game.WalkForwardAsync() : Game.StopAsync());

        /// <summary>
        /// Selects an object. The client's own selection is the client's alone - the protocol has
        /// no message for it - so the client's panels do not show it; but the choice stands for
        /// what plugins do with a selection here - <see cref="CurrentSelection"/>, and a use on the
        /// selection - until the player selects something in the client. That the client's own
        /// cannot be set is noted, once, with what was asked for.
        /// </summary>
        public void SelectItem(int objectId)
        {
            PreviousSelection = CurrentSelection;
            _chosen = unchecked((uint)objectId);
            _clientWhenChosen = Character.SelectedId;
            _runtime.SayOnce("select", $"used Actions.SelectItem on 0x{objectId:X8}: the game client's own selection cannot be set from outside it, so the client does not show it, but Decal plugins have it as the selection until the player selects something.");
        }

        /// <summary>
        /// Turns the character where it stands to face a heading, in degrees clockwise from north,
        /// by the game's turn keys (<see cref="Facing"/>): pressed through the overlay while plugins
        /// may act. True when the turn has begun; it ends within a few degrees, or gives up.
        /// </summary>
        public bool FaceHeading(double heading, bool bUnknown) => _runtime.Facing.Face(heading);

        public bool RadianFaceHeading(double heading, bool bUnknown) => FaceHeading(heading * 180.0 / Math.PI, bUnknown);

        public bool IsValidObject(int objectId) => _runtime.Host.World.TryGet(unchecked((uint)objectId), out _);

        public void AutoWield(int item) => UseItem(item, 0);

        public void AutoWield(int item, int slot, int explic, int notexplic) => UseItem(item, 0);

        public void AutoWield(int item, int slot, int explic, int notexplic, int zero1, int zero2) => UseItem(item, 0);

        /// <summary>
        /// Logs out the current character and returns to the character selection screen, as
        /// Decal's did - by the client's own Log Out where its keys can be pressed, else by the
        /// message the client sends for it. CharacterFilter's Logoff follows, Requested then
        /// Authorized, as for the player's own logout.
        /// </summary>
        public void Logout() => Send(Game.LogOutAsync());

        public void SetIdleTime(double timeout) => _runtime.NoteUnsupported("Actions.SetIdleTime");

        public void SetCursorPosition(int x, int y) => _runtime.NoteUnsupported("Actions.SetCursorPosition");

        public void SpellTabAdd(int tab, int index, int spellId) => _runtime.NoteUnsupported("Actions.SpellTabAdd");

        public void SpellTabDelete(int tab, int spellId) => _runtime.NoteUnsupported("Actions.SpellTabDelete");

        public void SalvagePanelAdd(int objectId) => _runtime.NoteUnsupported("Actions.SalvagePanelAdd");

        public void SalvagePanelSalvage() => _runtime.NoteUnsupported("Actions.SalvagePanelSalvage");

        /// <summary>Asks a player to join the fellowship, as its panel's Recruit does.</summary>
        public void FellowshipRecruit(int lObjectID) => Send(Game.FellowshipRecruitAsync(unchecked((uint)lObjectID)));

        /// <summary>Makes another member the leader.</summary>
        public void FellowshipGrantLeader(int lObjectID) => Send(Game.FellowshipAssignLeaderAsync(unchecked((uint)lObjectID)));

        /// <summary>Lets every member recruit, or only the leader.</summary>
        public void FellowshipSetOpen(bool IsOpen) => Send(Game.FellowshipSetOpenAsync(IsOpen));

        /// <summary>Leaves the fellowship.</summary>
        public void FellowshipQuit() => Send(Game.FellowshipQuitAsync(false));

        /// <summary>Ends the fellowship for everyone, as its leader; a member who is not the leader leaves it.</summary>
        public void FellowshipDisband() => Send(Game.FellowshipQuitAsync(true));

        /// <summary>Dismisses a member, as the leader.</summary>
        public void FellowshipDismiss(int lObjectID) => Send(Game.FellowshipDismissAsync(unchecked((uint)lObjectID)));

        public void TradeAccept() => _runtime.NoteUnsupported("Actions.Trade");

        public void TradeAdd(int objectId) => _runtime.NoteUnsupported("Actions.Trade");

        public void TradeDecline() => _runtime.NoteUnsupported("Actions.Trade");

        public void TradeReset() => _runtime.NoteUnsupported("Actions.Trade");

        public void TradeEnd() => _runtime.NoteUnsupported("Actions.Trade");

        public void VendorBuyAll() => _runtime.NoteUnsupported("Actions.Vendor");

        public void VendorAddBuyList(int templateId, int count) => _runtime.NoteUnsupported("Actions.Vendor");

        public void VendorClearBuyList() => _runtime.NoteUnsupported("Actions.Vendor");

        public void VendorSellAll() => _runtime.NoteUnsupported("Actions.Vendor");

        public void VendorAddSellList(int itemId) => _runtime.NoteUnsupported("Actions.Vendor");

        public void VendorClearSellList() => _runtime.NoteUnsupported("Actions.Vendor");

        public void AddSkillExperience(SkillType skill, int experience) => _runtime.NoteUnsupported("Actions.AddSkillExperience");

        public void AddAttributeExperience(AttributeType attrib, int experience) => _runtime.NoteUnsupported("Actions.AddAttributeExperience");

        public void AddVitalExperience(VitalType vital, int experience) => _runtime.NoteUnsupported("Actions.AddVitalExperience");

        /// <summary>Pointers into the client's memory. There is no client memory here.</summary>
        public IntPtr PhysicsObject(int objectId) => IntPtr.Zero;

        public IntPtr WeenieObject(int objectId) => IntPtr.Zero;

        public IntPtr UIElementLookup(UIElementType pUIElementType) => IntPtr.Zero;

        public void UIElementMove(UIElementType pUIElementType, int x, int y) => _runtime.NoteUnsupported("Actions.UIElementMove");

        public void UIElementResize(UIElementType pUIElementType, int width, int height) => _runtime.NoteUnsupported("Actions.UIElementResize");

        public Rectangle UIElementRegion(UIElementType pUIElementType) => Rectangle.Empty;

        public void Dispose()
        {
        }

        // ------------------------------------------------------------------- helpers

        /// <summary>
        /// Lets an action go. Decal's hooks were fire and forget; the host's answer comes back
        /// as a task, and a failure is already logged by the host, so it is only observed here
        /// to keep an unobserved fault from being reported later.
        /// </summary>
        private static void Send(Task<bool> sent)
        {
            if (!sent.IsCompleted)
                sent.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }

        private int AttributeValue(AttributeType type)
            => Character.Attributes.TryGetValue(AttributeId(type), out AttributeState a) ? unchecked((int)a.Base) : 0;

        private int VitalValue(VitalType type)
        {
            uint id = VitalId(type);
            if (!Character.Vitals.TryGetValue(id, out VitalState vital))
                return 0;

            return type switch
            {
                VitalType.CurrentHealth or VitalType.CurrentStamina or VitalType.CurrentMana => unchecked((int)vital.Current),
                _ => CoreManager.Current?.CharacterFilter.VitalBase((CharFilterVitalType)(id + 1)) ?? 0,
            };
        }

        /// <summary>Current and base attributes are numbered 1-6 and 7-12; the game's own ids are 1-6.</summary>
        private static uint AttributeId(AttributeType type) => (uint)(((int)type - 1) % 6 + 1);

        /// <summary>
        /// Current skills are numbered as the game numbers skills, 1 to 54, and base skills 50
        /// above that, from 56.
        /// </summary>
        private static uint SkillId(SkillType type)
        {
            int value = (int)type;
            return (uint)(value >= 56 ? value - 50 : value);
        }

        /// <summary>The host's vital id - 1, 3 or 5 - for any of Decal's names for health, stamina or mana.</summary>
        private static uint VitalId(VitalType type)
            => type switch
            {
                VitalType.MaximumHealth or VitalType.CurrentHealth or VitalType.BaseHealth => 1u,
                VitalType.MaximumStamina or VitalType.CurrentStamina or VitalType.BaseStamina => 3u,
                _ => 5u,
            };
    }

    /// <summary>A read-only number Decal's hooks indexed by an enum.</summary>
    public sealed class HookIndexer<IndexType> : MarshalByRefObject
    {
        private readonly Func<IndexType, int> _lookup;

        internal HookIndexer(Func<IndexType, int> lookup)
        {
            _lookup = lookup;
        }

        public int this[IndexType item] => _lookup(item);
    }
}
