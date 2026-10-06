using System;
using System.Collections.Generic;

namespace AC.Dat
{
    /// <summary>
    /// One of the client's chat emotes: the motion command it plays, by name, and its words -
    /// the line the character's own chat shows and the one sent for everyone else's.
    /// </summary>
    public sealed class ChatEmote
    {
        public ChatEmote(string command, string mine, string others)
        {
            Command = command ?? string.Empty;
            Mine = mine ?? string.Empty;
            Others = others ?? string.Empty;
        }

        /// <summary>The motion command's name, as the client's command table has it: "DrudgeDanceState", "Wave".</summary>
        public string Command { get; }

        /// <summary>What the character's own chat showed after "You ": "dance, "Look at me! I'm dancin crazy!"".</summary>
        public string Mine { get; }

        /// <summary>
        /// What everyone else reads after the character's name: "dances, "Look at me! I'm dancin
        /// crazy!"". "%p" stands for "his" or "her".
        /// </summary>
        public string Others { get; }

        public override string ToString() => Command;
    }

    /// <summary>
    /// The client's ChatPoseTable (0x0E000007): what a word between asterisks in a chat line -
    /// "*dance*", "*wave*" - makes the character do, and say.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two packed hash tables. The first names each pose a motion command: "dance" and "crazy
    /// dance" are DrudgeDanceState, "hi" and "howdy" Wave - 309 poses in the client's archive.
    /// The second gives each command its words (ChatEmoteData), the line for the character's own
    /// chat and the one for everyone else's - 74 of them, every one a motion ACE takes as a
    /// soul emote. Every string is a 16-bit length and its text, padded to a four-byte boundary.
    /// </para>
    /// <para>
    /// A pose is looked up ignoring case, as a player types it; one that differs only in case
    /// from another ("AFK") finds its own entry first.
    /// </para>
    /// </remarks>
    public sealed class ChatPoseTable
    {
        public const uint FileId = 0x0E000007;

        private readonly Dictionary<string, string> _poses = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _posesAnyCase = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ChatEmote> _emotes = new Dictionary<string, ChatEmote>(StringComparer.OrdinalIgnoreCase);

        private ChatPoseTable()
        {
        }

        /// <summary>Every pose and the command it names, as the table has them.</summary>
        public IReadOnlyDictionary<string, string> Poses => _poses;

        /// <summary>Every command with words, by its name, ignoring case.</summary>
        public IReadOnlyDictionary<string, ChatEmote> Emotes => _emotes;

        /// <summary>
        /// The emote a pose names - "dance", "come here" - with its words, or null for a pose the
        /// client does not have, or one whose command has no words.
        /// </summary>
        public ChatEmote Find(string pose)
        {
            if (string.IsNullOrEmpty(pose))
                return null;

            if (!_poses.TryGetValue(pose, out string command) && !_posesAnyCase.TryGetValue(pose, out command))
                return null;

            return _emotes.TryGetValue(command, out ChatEmote emote) ? emote : null;
        }

        /// <summary>The table from its file, or null when the file does not read as one.</summary>
        public static ChatPoseTable Parse(ReadOnlySpan<byte> data)
        {
            DatBinaryReader reader = new DatBinaryReader(data);
            if (!reader.TryReadUInt32(out _))       // the file's own id
                return null;

            ChatPoseTable table = new ChatPoseTable();

            if (!reader.TryReadHashTableHeader(out int poses))
                return null;

            for (int i = 0; i < poses; i++)
            {
                if (!TryReadString(ref reader, out string pose) || !TryReadString(ref reader, out string command))
                    return null;

                table._poses[pose] = command;
                table._posesAnyCase.TryAdd(pose, command);
            }

            if (!reader.TryReadHashTableHeader(out int emotes))
                return null;

            for (int i = 0; i < emotes; i++)
            {
                if (!TryReadString(ref reader, out string command)
                    || !TryReadString(ref reader, out string mine)
                    || !TryReadString(ref reader, out string others))
                    return null;

                table._emotes[command] = new ChatEmote(command, mine, others);
            }

            return table;
        }

        private static bool TryReadString(ref DatBinaryReader reader, out string value)
            => reader.TryReadString(out value) && reader.TryAlign();
    }
}
