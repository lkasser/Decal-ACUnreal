namespace AC.Host.World
{
    /// <summary>
    /// One active enchantment on the character: a buff, or a debuff something else put
    /// there.
    /// </summary>
    /// <remarks>
    /// Read from a live session rather than from a specification. Three of these arrived
    /// during one session and between them they pin the layout down: one was "Surge of
    /// Protection" for 12 seconds with the player as its own caster, and one was
    /// "Bafflement Other V" for 180 seconds with a creature as caster and a stat
    /// modifier of -30 - a monster debuffing the player. <see cref="DegradeLimit"/> was
    /// -666 in every one, which is the sentinel the retail client uses, and that is a
    /// useful thing to see land in the right place.
    ///
    /// The identity is the pair <see cref="SpellId"/> and <see cref="Layer"/>: the same
    /// spell can be on a character more than once at different layers, and a removal
    /// names both.
    /// </remarks>
    public sealed class Enchantment
    {
        public Enchantment(uint packedId)
        {
            PackedId = packedId;
        }

        /// <summary>Spell id and layer as one word, which is how a removal names it.</summary>
        public uint PackedId { get; }

        /// <summary>The spell. Not every id has a name in the client's own table.</summary>
        public ushort SpellId => (ushort)(PackedId & 0xFFFF);

        /// <summary>Which instance of this spell. The same spell can be present twice.</summary>
        public ushort Layer => (ushort)(PackedId >> 16);

        /// <summary>
        /// The spell's family, as the client's spell table numbers it: Strength Self I and
        /// Incantation of Strength Self share one, and only the strongest of a family counts.
        /// </summary>
        /// <remarks>
        /// On the wire this is a half-word followed by another half-word saying whether a
        /// spell-set id follows. Read as one word, as it once was, every category came out
        /// 0x10000 too high, since ACE always sets the flag; read as a half-word it matched the
        /// family in the client's spell table for all 391 enchantments of seven captured logins.
        /// </remarks>
        public uint Category { get; internal set; }

        /// <summary>The spell's level; within a family the higher wins.</summary>
        public uint PowerLevel { get; internal set; }

        /// <summary>
        /// Seconds since it was cast, as a negative number, when the server sent it. ACE ages
        /// it on every heartbeat and drops the enchantment once it has run its
        /// <see cref="Duration"/>, so a login lists buffs cast long before with large
        /// negative start times.
        /// </summary>
        public double StartTime { get; internal set; }

        /// <summary>How long it lasts, in seconds. Negative means it does not expire.</summary>
        public double Duration { get; internal set; }

        /// <summary>True for an enchantment that never runs out - one an item gives while worn.</summary>
        public bool IsPermanent => Duration < 0;

        /// <summary>
        /// Seconds it had left when the server sent it; infinite for a permanent one. Time
        /// since then is the caller's to subtract.
        /// </summary>
        public double RemainingWhenSent => IsPermanent ? double.PositiveInfinity : Duration + StartTime;

        /// <summary>Whatever put it there - the character itself for a self-buff.</summary>
        public uint CasterId { get; internal set; }

        public float DegradeModifier { get; internal set; }

        public float DegradeLimit { get; internal set; }

        public double LastTimeDegraded { get; internal set; }

        /// <summary>What kind of thing the modifier applies to.</summary>
        public uint StatModType { get; internal set; }

        /// <summary>Which attribute, skill or vital, by the server's own numbering.</summary>
        public uint StatModKey { get; internal set; }

        /// <summary>How much it changes it by. Negative for a debuff.</summary>
        public float StatModValue { get; internal set; }

        /// <summary>The equipment set that gives it, or zero; present only when the wire says so.</summary>
        public uint SpellSetId { get; internal set; }

        /// <summary>The penalty for dying, which scales every skill and vital down.</summary>
        public bool IsVitae => (StatModType & EnchantmentTypes.Vitae) != 0;

        /// <summary>True if this lowers something rather than raising it.</summary>
        public bool IsDebuff => StatModValue < 0;

        public override string ToString()
            => $"spell {SpellId} layer {Layer} for {Duration:F0}s from 0x{CasterId:X8}"
             + (StatModValue != 0 ? $" ({StatModValue:+0.##;-0.##} on key {StatModKey})" : string.Empty);
    }

    /// <summary>
    /// The bits of <see cref="Enchantment.StatModType"/>, by the server's own names (its
    /// EnchantmentTypeFlags). The low byte says what kind of thing is changed, the rest how.
    /// </summary>
    /// <remarks>
    /// The captured enchantments bear these out: Surge of Protection arrived as
    /// 0x02009004 - beneficial, additive, one statistic, an integer property - on key 308,
    /// the damage resistance rating, and a login's item spells on Endurance as 0x02009001.
    /// </remarks>
    public static class EnchantmentTypes
    {
        public const uint Attribute = 0x0001;
        public const uint SecondAtt = 0x0002;
        public const uint Int = 0x0004;
        public const uint Float = 0x0008;
        public const uint Skill = 0x0010;
        public const uint BodyDamageValue = 0x0020;
        public const uint BodyDamageVariance = 0x0040;
        public const uint BodyArmorValue = 0x0080;
        public const uint SingleStat = 0x1000;
        public const uint MultipleStat = 0x2000;
        public const uint Multiplicative = 0x4000;
        public const uint Additive = 0x8000;
        public const uint AttackSkills = 0x10000;
        public const uint DefenseSkills = 0x20000;
        public const uint Vitae = 0x00800000;
        public const uint Cooldown = 0x01000000;
        public const uint Beneficial = 0x02000000;
    }
}
