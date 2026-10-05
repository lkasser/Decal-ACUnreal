using System;

namespace Decal.Adapter.Wrappers
{
    // The filters' event arguments, with internal constructors as Decal's had. Those for
    // trades, vendors, fellowships and the like exist so plugins that subscribe load; the host
    // decodes none of those messages, so they are never raised.

    public class CreateObjectEventArgs : EventArgs
    {
        internal CreateObjectEventArgs(WorldObject created)
        {
            New = created;
        }

        public WorldObject New { get; }
    }

    public class ChangeObjectEventArgs : EventArgs
    {
        internal ChangeObjectEventArgs(WorldObject changed, WorldChangeType change)
        {
            Changed = changed;
            Change = change;
        }

        public WorldObject Changed { get; }

        public WorldChangeType Change { get; }
    }

    public class MoveObjectEventArgs : EventArgs
    {
        internal MoveObjectEventArgs(WorldObject moved)
        {
            Moved = moved;
        }

        public WorldObject Moved { get; }
    }

    public class ReleaseObjectEventArgs : EventArgs
    {
        internal ReleaseObjectEventArgs(WorldObject released)
        {
            Released = released;
        }

        public WorldObject Released { get; }
    }

    public class LoginEventArgs : EventArgs
    {
        internal LoginEventArgs(int id)
        {
            Id = id;
        }

        public int Id { get; }
    }

    public class LogoffEventArgs : EventArgs
    {
        internal LogoffEventArgs(LogoffEventType type)
        {
            Type = type;
        }

        public LogoffEventType Type { get; }
    }

    public class DeathEventArgs : EventArgs
    {
        internal DeathEventArgs(string text)
        {
            Text = text ?? string.Empty;
        }

        public string Text { get; }
    }

    public class ChangeVitalEventArgs : EventArgs
    {
        internal ChangeVitalEventArgs(CharFilterVitalType type, int amount)
        {
            Type = type;
            Amount = amount;
        }

        public CharFilterVitalType Type { get; }

        /// <summary>The vital's new current value.</summary>
        public int Amount { get; }
    }

    public class ChangeEnchantmentsEventArgs : EventArgs
    {
        internal ChangeEnchantmentsEventArgs(AddRemoveEventType type, EnchantmentWrapper enchantment)
        {
            Type = type;
            Enchantment = enchantment;
        }

        public AddRemoveEventType Type { get; }

        public EnchantmentWrapper Enchantment { get; }
    }

    public class ChangePortalModeEventArgs : EventArgs
    {
        internal ChangePortalModeEventArgs(PortalEventType type)
        {
            Type = type;
        }

        public PortalEventType Type { get; }
    }

    public class SpellCastEventArgs : EventArgs
    {
        internal SpellCastEventArgs(CastEventType type, int spellId, int targetId)
        {
            EventType = type;
            SpellId = spellId;
            TargetId = targetId;
        }

        public int SpellId { get; }

        public int TargetId { get; }

        public CastEventType EventType { get; set; }
    }

    public class ChangeSpellbarEventArgs : EventArgs
    {
        internal ChangeSpellbarEventArgs(AddRemoveEventType type, int tab, int slot, int spellId)
        {
            Type = type;
            Tab = tab;
            Slot = slot;
            SpellId = spellId;
        }

        public AddRemoveEventType Type { get; }

        public int Tab { get; }

        public int Slot { get; }

        public int SpellId { get; }
    }

    public class SpellbookEventArgs : EventArgs
    {
        internal SpellbookEventArgs(AddRemoveEventType type, int spell)
        {
            Type = type;
            Spell = spell;
        }

        public AddRemoveEventType Type { get; }

        public int Spell { get; }
    }

    public class ChangePlayerEventArgs : EventArgs
    {
        internal ChangePlayerEventArgs(PlayerModifyEventType type, int stat)
        {
            Type = type;
            Stat = stat;
        }

        public PlayerModifyEventType Type { get; }

        public int Stat { get; }
    }

    public class ChangeFellowshipEventArgs : EventArgs
    {
        internal ChangeFellowshipEventArgs(FellowshipEventType type, int id)
        {
            Type = type;
            Id = id;
        }

        public FellowshipEventType Type { get; }

        public int Id { get; }
    }

    public class ChangeExperienceEventArgs : EventArgs
    {
        internal ChangeExperienceEventArgs(PlayerXPEventType type, int amount)
        {
            Type = type;
            Amount = amount;
        }

        public PlayerXPEventType Type { get; }

        public int Amount { get; }
    }

    public class ChangeShortcutEventArgs : EventArgs
    {
        internal ChangeShortcutEventArgs(AddRemoveEventType type, int slot, int objectId)
        {
            Type = type;
            Slot = slot;
            ObjectId = objectId;
        }

        public AddRemoveEventType Type { get; }

        public int Slot { get; }

        public int ObjectId { get; }
    }

    public class SettingsEventArgs : EventArgs
    {
        internal SettingsEventArgs(int setting)
        {
            Setting = setting;
        }

        public int Setting { get; }
    }

    public class ChangeSettingsFlagsEventArgs : EventArgs
    {
        internal ChangeSettingsFlagsEventArgs(int settings)
        {
            Settings = settings;
        }

        public int Settings { get; }
    }

    public class ChangeOptionEventArgs : EventArgs
    {
        internal ChangeOptionEventArgs(int option, int value)
        {
            Option = option;
            Value = value;
        }

        public int Option { get; }

        public int Value { get; }
    }

    public class StatusMessageEventArgs : EventArgs
    {
        internal StatusMessageEventArgs(int type, string text)
        {
            Type = type;
            Text = text ?? string.Empty;
        }

        public int Type { get; }

        public string Text { get; }
    }

    public class AcceptTradeEventArgs : EventArgs
    {
        internal AcceptTradeEventArgs(int targetId)
        {
            TargetId = targetId;
        }

        public int TargetId { get; }
    }

    public class AddTradeItemEventArgs : EventArgs
    {
        internal AddTradeItemEventArgs(int itemId, int sideId)
        {
            ItemId = itemId;
            SideId = sideId;
        }

        public int ItemId { get; }

        public int SideId { get; }
    }

    public class ApproachVendorEventArgs : EventArgs
    {
        internal ApproachVendorEventArgs(int merchantId, Vendor vendor)
        {
            MerchantId = merchantId;
            Vendor = vendor;
        }

        public int MerchantId { get; }

        public Vendor Vendor { get; }
    }

    public class DeclineTradeEventArgs : EventArgs
    {
        internal DeclineTradeEventArgs(int traderId)
        {
            TraderId = traderId;
        }

        public int TraderId { get; }
    }

    public class EndTradeEventArgs : EventArgs
    {
        internal EndTradeEventArgs(int reasonId)
        {
            ReasonId = reasonId;
        }

        public int ReasonId { get; }
    }

    public class EnterTradeEventArgs : EventArgs
    {
        internal EnterTradeEventArgs(int traderId, int tradeeId)
        {
            TraderId = traderId;
            TradeeId = tradeeId;
        }

        public int TraderId { get; }

        public int TradeeId { get; }
    }

    public class FailToAddTradeItemEventArgs : EventArgs
    {
        internal FailToAddTradeItemEventArgs(int itemId, int reasonId)
        {
            ItemId = itemId;
            ReasonId = reasonId;
        }

        public int ItemId { get; }

        public int ReasonId { get; }
    }

    public class ResetTradeEventArgs : EventArgs
    {
        internal ResetTradeEventArgs(int traderId)
        {
            TraderId = traderId;
        }

        public int TraderId { get; }
    }
}
