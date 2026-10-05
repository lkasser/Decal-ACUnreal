using System;
using AC.Host.World;

namespace Decal.Adapter.Hosting
{
    /// <summary>
    /// Writes a chat message out as the client's chat window showed it, which is the text
    /// Decal handed plugins.
    /// </summary>
    /// <remarks>
    /// Plugins match chat with regular expressions written against that exact text - the
    /// name link around a player, "tells you", the closing newline - so it is reproduced
    /// rather than summarised. The host's <see cref="ChatLines"/> writes it, so a Decal plugin
    /// and Virindi Tank's meta read the same line.
    /// </remarks>
    internal static class ChatText
    {
        public static string Format(ChatMessage message, uint playerId = 0, Func<string, uint> nameToId = null, string playerName = null)
            => ChatLines.Format(message, playerId, nameToId, playerName);

        /// <summary>The chat colour the client drew it in: the server's chat type, for everything that has one.</summary>
        public static int Color(ChatMessage message) => ChatLines.Color(message);
    }
}
