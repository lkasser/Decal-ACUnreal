using System.Text;

namespace AC.Protocol
{
    /// <summary>
    /// The game's text: Windows-1252, as the client, the server and the dat files write every
    /// string. Latin-1 would read its curly quotes and dashes - the one in Blackmoor's Favor - as
    /// control characters, which show as nothing or "?".
    /// </summary>
    public static class AcEncoding
    {
        /// <summary>Windows-1252; its five unassigned bytes round-trip as the control characters of the same number.</summary>
        public static Encoding Text { get; } = CodePagesEncodingProvider.Instance.GetEncoding(1252) ?? Encoding.Latin1;
    }
}
