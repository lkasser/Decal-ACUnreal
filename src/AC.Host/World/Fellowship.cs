using System;
using System.Collections.Generic;

namespace AC.Host.World
{
    /// <summary>One member of the character's fellowship, as the server last described them.</summary>
    /// <remarks>
    /// The server describes every member in full when the fellowship forms or changes, and after
    /// that sends a member's vitals only to members whose fellowship panel is open - which is
    /// why a helper has to know when the numbers were last true (<see cref="LastUpdated"/>).
    /// </remarks>
    public sealed class Fellow
    {
        public Fellow(uint id)
        {
            Id = id;
        }

        public uint Id { get; }

        public string Name { get; internal set; } = string.Empty;

        public uint Level { get; internal set; }

        public uint MaxHealth { get; internal set; }

        public uint MaxStamina { get; internal set; }

        public uint MaxMana { get; internal set; }

        public uint CurrentHealth { get; internal set; }

        public uint CurrentStamina { get; internal set; }

        public uint CurrentMana { get; internal set; }

        /// <summary>
        /// Whether the member shares loot, as the server's share-loot word says (anything but
        /// zero). Virindi Tank read it to decide how soon a fellow's corpse could be opened.
        /// </summary>
        public bool ShareLoot { get; internal set; }

        /// <summary>Host clock when these numbers arrived.</summary>
        public DateTimeOffset LastUpdated { get; internal set; }

        public override string ToString()
            => $"0x{Id:X8} \"{Name}\" {CurrentHealth}/{MaxHealth} {CurrentStamina}/{MaxStamina} {CurrentMana}/{MaxMana}";
    }

    /// <summary>The character's fellowship, as far as the server has said.</summary>
    public interface IFellowshipView
    {
        /// <summary>True while the character is in a fellowship.</summary>
        bool IsMember { get; }

        /// <summary>The fellowship's name, or empty.</summary>
        string Name { get; }

        /// <summary>The leader's object id, or 0.</summary>
        uint LeaderId { get; }

        /// <summary>Every member, the character included, by object id.</summary>
        IReadOnlyDictionary<uint, Fellow> Members { get; }

        /// <summary>
        /// Whether the client last said its fellowship panel is open. The server sends fellows'
        /// vitals as they change only while it is, so without it the numbers go stale.
        /// </summary>
        bool PanelOpen { get; }
    }

    /// <summary>The mutable fellowship behind <see cref="IFellowshipView"/>, changed only by the decoder.</summary>
    public sealed class FellowshipState : IFellowshipView
    {
        private readonly Dictionary<uint, Fellow> _members = new Dictionary<uint, Fellow>();

        public bool IsMember { get; private set; }

        public string Name { get; private set; } = string.Empty;

        public uint LeaderId { get; private set; }

        public IReadOnlyDictionary<uint, Fellow> Members => _members;

        public bool PanelOpen { get; internal set; }

        /// <summary>A full description: the fellowship is exactly these members now.</summary>
        internal void Replace(string name, uint leaderId, IEnumerable<Fellow> members)
        {
            _members.Clear();
            foreach (Fellow fellow in members)
                _members[fellow.Id] = fellow;

            Name = name ?? string.Empty;
            LeaderId = leaderId;
            IsMember = true;
        }

        /// <summary>One member described again, or described for the first time.</summary>
        internal void Update(Fellow fellow)
        {
            _members[fellow.Id] = fellow;
            IsMember = true;
        }

        internal void Remove(uint id) => _members.Remove(id);

        /// <summary>The character is no longer in a fellowship.</summary>
        internal void Clear()
        {
            _members.Clear();
            Name = string.Empty;
            LeaderId = 0;
            IsMember = false;
        }
    }
}
