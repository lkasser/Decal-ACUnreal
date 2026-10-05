namespace AC.Host.World
{
    /// <summary>
    /// What kind of damage. Flags in the protocol, one at a time on the wire.
    /// </summary>
    public static class DamageTypes
    {
        public const uint Slash = 0x01;
        public const uint Pierce = 0x02;
        public const uint Bludgeon = 0x04;
        public const uint Cold = 0x08;
        public const uint Fire = 0x10;
        public const uint Acid = 0x20;
        public const uint Electric = 0x40;
        public const uint Health = 0x80;

        /// <summary>A readable name, or the raw value if it is not a single known type.</summary>
        public static string Name(uint damageType) => damageType switch
        {
            Slash => "slashing",
            Pierce => "piercing",
            Bludgeon => "bludgeoning",
            Cold => "cold",
            Fire => "fire",
            Acid => "acid",
            Electric => "electric",
            Health => "health",
            0 => "unspecified",
            _ => $"0x{damageType:X2}",
        };
    }

    /// <summary>
    /// Where a blow landed. The server's own numbering, which runs head to foot.
    /// </summary>
    public static class BodyParts
    {
        public static string Name(uint location) => location switch
        {
            0 => "nowhere in particular",
            1 => "head",
            2 => "chest",
            3 => "abdomen",
            4 => "upper arm",
            5 => "lower arm",
            6 => "hand",
            7 => "upper leg",
            8 => "lower leg",
            9 => "foot",
            _ => $"part {location}",
        };
    }

    /// <summary>
    /// A blow that landed on the character.
    /// </summary>
    /// <remarks>
    /// The layout came off thirteen of these in one session, and three things in the
    /// data say the fields are where they look. A creature called "Inferno" reports
    /// damage type 0x10, which is fire. The body part is always between 0 and 9. And the
    /// one blow with <see cref="IsCritical"/> set did 15 damage where every other blow
    /// in the session did between 1 and 4 - while <see cref="Percentage"/> tracked the
    /// amount at a constant ratio throughout, which is what a fraction of one
    /// character's health looks like.
    /// </remarks>
    public sealed class DamageTaken
    {
        public DamageTaken(string attacker, uint damageType, double percentage, uint amount, uint location, bool critical)
        {
            Attacker = attacker;
            DamageType = damageType;
            Percentage = percentage;
            Amount = amount;
            Location = location;
            IsCritical = critical;
        }

        /// <summary>What hit the character, by name. The protocol sends no id here.</summary>
        public string Attacker { get; }

        public uint DamageType { get; }

        /// <summary>The fraction of the character's health this took, from 0 to 1.</summary>
        public double Percentage { get; }

        public uint Amount { get; }

        /// <summary>Which body part, by the server's numbering.</summary>
        public uint Location { get; }

        public bool IsCritical { get; }

        /// <summary>
        /// Deliberately not culture-sensitive. A percent format writes "4.0 %" in one
        /// culture and "4.0%" in another, and a log line that changes shape with the
        /// machine it runs on is a nuisance to read and worse to match on.
        /// </summary>
        public override string ToString()
            => string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0} hit {1} for {2} {3} ({4:0.0}%){5}",
                Attacker,
                BodyParts.Name(Location),
                Amount,
                DamageTypes.Name(DamageType),
                Percentage * 100,
                IsCritical ? " - critical" : string.Empty);
    }

    /// <summary>
    /// A blow the character struck: the mirror of <see cref="DamageTaken"/>.
    /// </summary>
    /// <remarks>
    /// The layout is ACE's writer for AttackerNotification: the defender's name, the damage
    /// type, the fraction of the defender's health taken, the amount, a critical flag, then
    /// the attack conditions. It is the defender's message without the body part, and the
    /// defender's message as captured matches ACE's writer for it word for word - which is
    /// the reason to take this one from the same writer until a session with the character
    /// fighting hand to hand has been recorded.
    /// </remarks>
    public sealed class DamageDealt
    {
        public DamageDealt(string defender, uint damageType, double percentage, uint amount, bool critical)
        {
            Defender = defender;
            DamageType = damageType;
            Percentage = percentage;
            Amount = amount;
            IsCritical = critical;
        }

        /// <summary>What was hit, by name. As with a blow taken, the protocol sends no id.</summary>
        public string Defender { get; }

        public uint DamageType { get; }

        /// <summary>The fraction of the defender's health this took, from 0 to 1.</summary>
        public double Percentage { get; }

        public uint Amount { get; }

        public bool IsCritical { get; }

        public override string ToString()
            => string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "hit {0} for {1} {2} ({3:0.0}%){4}",
                Defender,
                Amount,
                DamageTypes.Name(DamageType),
                Percentage * 100,
                IsCritical ? " - critical" : string.Empty);
    }
}
