namespace AC.Host.Decoding
{
    /// <summary>
    /// Server-to-client message opcodes the host decodes. Values are the protocol's;
    /// names follow ACE's GameMessageOpcode where one exists.
    /// </summary>
    public static class Opcodes
    {
        public const uint InventoryRemoveObject = 0x0024;
        public const uint SetStackSize = 0x0197;
        public const uint EmoteText = 0x01E0;

        /// <summary>An emote with a motion ("*dance*"): laid out as EmoteText is.</summary>
        public const uint SoulEmote = 0x01E2;
        public const uint HearSpeech = 0x02BB;
        public const uint HearRangedSpeech = 0x02BC;

        public const uint PrivateUpdatePropertyInt = 0x02CD;
        public const uint PublicUpdatePropertyInt = 0x02CE;
        public const uint PrivateUpdatePropertyInt64 = 0x02CF;
        public const uint PublicUpdatePropertyInt64 = 0x02D0;
        public const uint PrivateUpdatePropertyBool = 0x02D1;
        public const uint PublicUpdatePropertyBool = 0x02D2;
        public const uint PrivateUpdatePropertyFloat = 0x02D3;
        public const uint PublicUpdatePropertyFloat = 0x02D4;
        public const uint PrivateUpdatePropertyString = 0x02D5;
        public const uint PublicUpdatePropertyString = 0x02D6;
        public const uint PrivateUpdatePropertyDataId = 0x02D7;
        public const uint PublicUpdatePropertyDataId = 0x02D8;
        public const uint PrivateUpdatePropertyInstanceId = 0x02D9;
        public const uint PublicUpdatePropertyInstanceId = 0x02DA;
        public const uint PrivateUpdatePosition = 0x02DB;
        public const uint PublicUpdatePosition = 0x02DC;
        public const uint PrivateUpdateSkill = 0x02DD;
        public const uint PrivateUpdateAttribute = 0x02E3;
        public const uint PrivateUpdateVital = 0x02E7;
        public const uint PublicUpdateVital = 0x02E8;
        public const uint PrivateUpdateAttribute2ndLevel = 0x02E9;

        public const uint ObjDescEvent = 0xF625;

        /// <summary>
        /// The character has left the world: ACE's answer once a logoff is done, followed by the
        /// character list and the server's name. Carries nothing.
        /// </summary>
        public const uint CharacterLogOff = 0xF653;

        /// <summary>The account's characters, sent at login and again whenever the character leaves the world.</summary>
        public const uint CharacterList = 0xF658;

        public const uint ObjectCreate = 0xF745;
        public const uint PlayerCreate = 0xF746;
        public const uint ObjectDelete = 0xF747;
        public const uint UpdatePosition = 0xF748;
        public const uint PickupEvent = 0xF74A;

        /// <summary>An object's physics state changed: a door opening, a projectile vanishing.</summary>
        public const uint SetState = 0xF74B;

        /// <summary>An object was set moving: a jump, or a projectile coming to rest.</summary>
        public const uint VectorUpdate = 0xF74E;

        public const uint Sound = 0xF750;
        public const uint PlayEffect = 0xF755;

        /// <summary>The character is being teleported: into portal space until the client says it has arrived.</summary>
        public const uint PlayerTeleport = 0xF751;

        /// <summary>Someone nearby died: the sentence, the one who died, the one who killed them.</summary>
        public const uint PlayerKilled = 0x019E;

        /// <summary>Chat in the server's own rooms - allegiance, general, trade and the rest. Both directions.</summary>
        public const uint TurbineChat = 0xF7DE;

        /// <summary>An object attached to another: an arrow nocked, a weapon drawn.</summary>
        public const uint ParentEvent = 0xF749;

        /// <summary>Motion / MovementEvent. The most common message in a live session.</summary>
        public const uint Motion = 0xF74C;
        public const uint GameEvent = 0xF7B0;
        public const uint GameAction = 0xF7B1;
        public const uint UpdateObject = 0xF7DB;

        /// <summary>The account is being thrown off, perhaps with why.</summary>
        public const uint AccountBoot = 0xF7DC;

        public const uint ServerMessage = 0xF7E0;
        public const uint ServerName = 0xF7E1;
    }

    /// <summary>Event types carried inside a GameEvent (0xF7B0) envelope.</summary>
    public static class GameEvents
    {
        public const uint PlayerDescription = 0x0013;
        public const uint InventoryPutObjInContainer = 0x0022;
        public const uint WieldObject = 0x0023;
        public const uint IdentifyObjectResponse = 0x00C9;
        public const uint ViewContents = 0x0196;
        public const uint UpdateHealth = 0x01C0;
        public const uint WeenieError = 0x028A;
        public const uint WeenieErrorWithString = 0x028B;
        public const uint Tell = 0x02BD;
        public const uint CommunicationTransientString = 0x02EB;

        /// <summary>
        /// An action has finished, with the error that ended it or zero for success.
        /// The only reliable signal that it is safe to send the next one.
        /// </summary>
        public const uint UseDone = 0x01C7;

        /// <summary>A container on the ground is no longer open. Carries its id.</summary>
        public const uint CloseGroundContainer = 0x0052;

        /// <summary>An item has left the inventory for the ground. Carries its id.</summary>
        public const uint InventoryPutObjectIn3D = 0x019A;

        /// <summary>
        /// A move that did not happen - a pick-up, a move between packs, a drop. Carries the
        /// item id and an error, often zero. First seen for the one drop of three the server
        /// refused; ACE sends it for every move it turns down.
        /// </summary>
        public const uint InventoryServerSaveFailed = 0x00A0;

        // Combat. Named here even where there is no decoder yet, so a capture can be
        // read without looking the numbers up again.
        public const uint AttackDone = 0x01A7;
        public const uint VictimNotification = 0x01AC;
        public const uint KillerNotification = 0x01AD;
        public const uint AttackerNotification = 0x01B1;
        public const uint DefenderNotification = 0x01B2;
        public const uint EvasionAttackerNotification = 0x01B3;
        public const uint EvasionDefenderNotification = 0x01B4;

        /// <summary>The next swing or shot of a repeating attack has begun. Carries nothing.</summary>
        public const uint CombatCommenceAttack = 0x01B8;

        // Enchantments, which is what a buffing plugin has to track, and the spellbook.
        public const uint MagicUpdateSpell = 0x02C1;
        public const uint MagicRemoveSpell = 0x01A8;
        public const uint MagicPurgeBadEnchantments = 0x0312;
        public const uint MagicUpdateEnchantment = 0x02C2;
        public const uint MagicRemoveEnchantment = 0x02C3;
        public const uint MagicUpdateMultipleEnchantments = 0x02C4;
        public const uint MagicRemoveMultipleEnchantments = 0x02C5;
        public const uint MagicPurgeEnchantments = 0x02C6;
        public const uint MagicDispelEnchantment = 0x02C7;
        public const uint MagicDispelMultipleEnchantments = 0x02C8;

        public const uint SetTurbineChatChannels = 0x0295;

        /// <summary>A vendor's wares: the vendor, its terms, then everything it sells. Opens its window.</summary>
        public const uint ApproachVendor = 0x0062;

        /// <summary>Fellowship and allegiance-hierarchy chat: the channel, the sender, the text.</summary>
        public const uint ChannelBroadcast = 0x0147;

        // The fellowship, by ACE's GameEventType. Laid out from ACE's writers: no recorded
        // session has the character in a fellowship.

        /// <summary>Every member and the fellowship's settings.</summary>
        public const uint FellowshipFullUpdate = 0x02BE;

        /// <summary>The fellowship is gone. Carries nothing.</summary>
        public const uint FellowshipDisband = 0x02BF;

        /// <summary>One member described again: joining, levelling, or their vitals changing.</summary>
        public const uint FellowshipUpdateFellow = 0x02C0;

        /// <summary>A member left. Carries their id.</summary>
        public const uint FellowshipQuit = 0x00A3;

        /// <summary>A member was dismissed. Carries their id.</summary>
        public const uint FellowshipDismiss = 0x00A4;
    }

    /// <summary>
    /// What stance the character is in. Values from the server's own CombatMode enum;
    /// they read like flags but the wire carries one of them.
    /// </summary>
    public enum CombatMode : uint
    {
        Undefined = 0x00,
        NonCombat = 0x01,
        Melee = 0x02,
        Missile = 0x04,
        Magic = 0x08,
    }

    /// <summary>
    /// Where a blow is aimed. The server's own AttackHeight numbering, which Virindi Tank's
    /// "DefaultMeleeAttackHeight" option uses too.
    /// </summary>
    public enum AttackHeight : uint
    {
        High = 1,
        Medium = 2,
        Low = 3,
    }

    /// <summary>
    /// Equipment slots (the server's EquipMask). The weapon slots are the ones a wield names;
    /// the rest are here so a slot read off an item can be told apart from them.
    /// </summary>
    public static class EquipMasks
    {
        public const uint MeleeWeapon = 0x00100000;

        /// <summary>The left hand: a shield, or the second weapon when dual wielding.</summary>
        public const uint Shield = 0x00200000;

        public const uint MissileWeapon = 0x00400000;
        public const uint MissileAmmo = 0x00800000;

        /// <summary>A wand, staff or orb.</summary>
        public const uint Held = 0x01000000;

        public const uint TwoHanded = 0x02000000;

        /// <summary>Every slot that holds something to fight or cast with, ammunition aside.</summary>
        public const uint Weapons = MeleeWeapon | Shield | MissileWeapon | Held | TwoHanded;
    }

    /// <summary>The few server errors (WeenieError) the host has reason to name.</summary>
    public static class WeenieErrors
    {
        /// <summary>
        /// What AttackDone carries when an attack sequence has ended - the target died, went
        /// out of reach, or the attack was cancelled. ACE ends every sequence with it.
        /// </summary>
        public const uint ActionCancelled = 0x0036;
    }

    /// <summary>Client-to-server action types, for observing what the player does.</summary>
    public static class GameActions
    {
        /// <summary>Swing at a creature. Target, attack height, power from 0 to 1.</summary>
        public const uint TargetedMeleeAttack = 0x0008;

        /// <summary>Shoot at a creature. Target, attack height, accuracy from 0 to 1.</summary>
        public const uint TargetedMissileAttack = 0x000A;

        /// <summary>Stop attacking. Carries nothing.</summary>
        public const uint CancelAttack = 0x01B7;

        public const uint PutItemInContainer = 0x0019;

        /// <summary>Move part or all of one stack onto another. Source, target, amount.</summary>
        public const uint StackableMerge = 0x0054;

        /// <summary>The fellowship panel opened (1) or closed (0): vitals of fellows follow only while open.</summary>
        public const uint FellowshipUpdateRequest = 0x00A6;

        /// <summary>The salvage panel's Salvage button. The tool, a count, then the items.</summary>
        public const uint CreateTinkeringTool = 0x027D;
        public const uint GetAndWieldItem = 0x001A;
        public const uint DropItem = 0x001B;
        public const uint Use = 0x0036;
        public const uint UseWithTarget = 0x0035;
        public const uint IdentifyObject = 0x00C8;
        public const uint Talk = 0x0015;
        public const uint LoginComplete = 0x00A1;

        /// <summary>What a buffing or combat plugin sends. Target, then spell.</summary>
        public const uint CastTargetedSpell = 0x004A;

        public const uint CastUntargetedSpell = 0x0048;
        public const uint ChangeCombatMode = 0x0053;
        public const uint QueryHealth = 0x01BF;
        public const uint NoLongerViewingContents = 0x0195;

        /// <summary>Where the client says it is. By far the most frequent thing it sends.</summary>
        public const uint AutonomousPosition = 0xF753;

        /// <summary>What the client's body is doing: the movement command itself.</summary>
        public const uint MoveToState = 0xF61C;

        public const uint Jump = 0xF61B;

        // What the game client sends for the chat box's own commands; ACE's GameActionType.
        public const uint SetAfkMode = 0x000F;
        public const uint SetAfkMessage = 0x0010;
        public const uint TeleToPklArena = 0x0026;
        public const uint TeleToPkArena = 0x0027;
        public const uint Tell = 0x005D;
        public const uint TeleToLifestone = 0x0063;
        public const uint ChatChannel = 0x0147;
        public const uint QueryAge = 0x01C2;
        public const uint QueryBirth = 0x01C4;
        public const uint Emote = 0x01DF;
        public const uint SoulEmote = 0x01E1;
        public const uint TeleToHouse = 0x0262;
        public const uint TeleToMansion = 0x0278;
        public const uint Suicide = 0x0279;
        public const uint TeleToMarketPlace = 0x028D;
        public const uint EnterPkLite = 0x028F;
        public const uint QueryMotd = 0x0255;
        public const uint RecallAllegianceHometown = 0x02AB;

        // The character's own settings, as the client keeps the server told: Decal's messages.xml
        // lays them out, and its character filter read them.

        /// <summary>The options panel as a whole: CharacterOptionData, as the login describes it.</summary>
        public const uint SetCharacterOptions = 0x01A1;

        /// <summary>An object put on the shortcut bar: slot, object, and a word of spell.</summary>
        public const uint AddShortCut = 0x019C;

        /// <summary>A shortcut taken off: the slot.</summary>
        public const uint RemoveShortCut = 0x019D;

        /// <summary>A spell put on a spell bar: the spell, its place, the bar.</summary>
        public const uint AddSpellFavorite = 0x01E3;

        /// <summary>A spell taken off a spell bar: the spell, the bar.</summary>
        public const uint RemoveSpellFavorite = 0x01E4;
    }

    /// <summary>Object description flags (the server's ObjectDescriptionFlag).</summary>
    public static class DescriptionFlags
    {
        public const uint Openable = 0x00000001;
        public const uint Inscribable = 0x00000002;
        public const uint Stuck = 0x00000004;
        public const uint Player = 0x00000008;
        public const uint Attackable = 0x00000010;
        public const uint PlayerKiller = 0x00000020;
        public const uint HiddenAdmin = 0x00000040;
        public const uint UiHidden = 0x00000080;
        public const uint Book = 0x00000100;
        public const uint Vendor = 0x00000200;
        public const uint PkSwitch = 0x00000400;
        public const uint NpkSwitch = 0x00000800;
        public const uint Door = 0x00001000;
        public const uint Corpse = 0x00002000;
        public const uint LifeStone = 0x00004000;
        public const uint Food = 0x00008000;
        public const uint Healer = 0x00010000;
        public const uint Lockpick = 0x00020000;
        public const uint Portal = 0x00040000;
        public const uint Admin = 0x00100000;
        public const uint FreePkStatus = 0x00200000;
        public const uint ImmuneCellRestrictions = 0x00400000;
        public const uint RequiresPackSlot = 0x00800000;
        public const uint Retained = 0x01000000;
        public const uint PkLiteStatus = 0x02000000;
        public const uint IncludesSecondHeader = 0x04000000;
        public const uint BindStone = 0x08000000;
        public const uint VolatileRare = 0x10000000;
        public const uint WieldOnUse = 0x20000000;
        public const uint WieldLeft = 0x40000000;
    }

    /// <summary>Item type bits (the server's ItemType).</summary>
    public static class ItemTypes
    {
        public const uint MeleeWeapon = 0x00000001;
        public const uint Armor = 0x00000002;
        public const uint Clothing = 0x00000004;
        public const uint Jewelry = 0x00000008;
        public const uint Creature = 0x00000010;
        public const uint Food = 0x00000020;
        public const uint Money = 0x00000040;
        public const uint Misc = 0x00000080;
        public const uint MissileWeapon = 0x00000100;
        public const uint Container = 0x00000200;
        public const uint Useless = 0x00000400;
        public const uint Gem = 0x00000800;
        public const uint SpellComponents = 0x00001000;
        public const uint Writable = 0x00002000;
        public const uint Key = 0x00004000;
        public const uint Caster = 0x00008000;
        public const uint Portal = 0x00010000;
        public const uint Lockable = 0x00020000;
        public const uint PromissoryNote = 0x00040000;
        public const uint ManaStone = 0x00080000;
        public const uint Service = 0x00100000;
        public const uint MagicWieldable = 0x00200000;
        public const uint CraftCookingBase = 0x00400000;
        public const uint CraftAlchemyBase = 0x00800000;
        public const uint CraftFletchingBase = 0x02000000;
        public const uint CraftAlchemyIntermediate = 0x04000000;
        public const uint CraftFletchingIntermediate = 0x08000000;
        public const uint LifeStone = 0x10000000;
        public const uint TinkeringTool = 0x20000000;
        public const uint TinkeringMaterial = 0x40000000;
        public const uint Gameboard = 0x80000000;
    }
}
