using System;
using AC.Host.Decoding;
using AC.Protocol;

namespace AC.Host.Transport
{
    /// <summary>
    /// A transport that can keep a line the player typed from reaching the server, so that a
    /// plugin's command - "@vt start" - is carried out by the plugin and never said aloud or
    /// answered by the server as an unknown command.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Decal took such lines out of the retail client's chat box before the client saw them.
    /// Nothing can reach into AC:Unreal's chat box, so the line is caught later, on its way to
    /// the server: the client sends typed text as a Talk action, and the relay takes that one
    /// message out of the client's packet (<see cref="AC.Proxy.ClientStreamRewriter.Withhold"/>).
    /// The decision has to be made there and then, on the relay's own thread, before the
    /// packet goes - which is why the host hands over a quick test rather than being asked.
    /// </para>
    /// <para>
    /// Optional, like everything a transport may or may not be able to do: a capture cannot
    /// keep anything from a server that is not there, and does not implement it.
    /// </para>
    /// </remarks>
    public interface ITypedCommandSource
    {
        /// <summary>
        /// Whether a typed line is a plugin's command, and so to be kept from the server. Set by
        /// the host; called on the relay's thread, so it must be quick and must not block.
        /// </summary>
        Func<string, bool> IsPluginCommand { get; set; }

        /// <summary>A line kept from the server because it was a plugin's command. Raised on the relay's thread.</summary>
        event EventHandler<string> CommandTyped;
    }

    /// <summary>Reading what the player typed out of the message the client sends it in.</summary>
    public static class TypedLine
    {
        /// <summary>
        /// The text of a Talk action - what the client sends for a line typed into its chat box
        /// that it does not handle itself - given its opcode and the bytes after it: the action's
        /// ordering sequence, its type, then the text as a length-prefixed string.
        /// </summary>
        public static bool TryRead(uint opcode, ReadOnlySpan<byte> payload, out string text)
        {
            text = null;
            if (opcode != Opcodes.GameAction)
                return false;

            SpanReader reader = new SpanReader(payload);
            return reader.TrySkip(4)
                && reader.TryReadUInt32(out uint type) && type == GameActions.Talk
                && reader.TryReadString(out text);
        }
    }
}
