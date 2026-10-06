using System.Text.RegularExpressions;

namespace AC.Host.Plugins
{
    /// <summary>
    /// The markup the old game client read in a chat line: a link around some of the text, which
    /// AC:Unreal shows as it is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "&lt;Tell:IIDString:id:name&gt;text&lt;\Tell&gt;" - the retail client drew the text as a link,
    /// and the markup not at all. The server wrote it around a speaker's name - "&lt;Tell:IIDString:
    /// 1343111160:Character Y&gt;Character Y&lt;\Tell&gt; says, ..." - so that a click opened a tell; Decal raised a
    /// click on one as ChatNameClicked, with the id and the name, and plugins wrote their own:
    /// Mag-Tools and Virindi Tank put one around an item line, under the id 221112, so a click
    /// selects the item. Mag-Tools writes its closing tag with two backslashes, "&lt;\\Tell&gt;",
    /// which the client took as well.
    /// </para>
    /// <para>
    /// AC:Unreal's chat has no such links - nothing in it reads "IIDString" - and a line the host
    /// puts there goes as a message from the server, which names no link either. So the line
    /// shows the text the link was around, as the old client showed it, and cannot be clicked.
    /// Nothing else that looks like a tag is touched: "&lt;{Mag-Tools}&gt;:" is Mag-Tools' own
    /// name for itself, to be shown as written.
    /// </para>
    /// </remarks>
    public static class ChatMarkup
    {
        private static readonly Regex Link = new Regex(@"<Tell:[^<>]*>|<\\+Tell>", RegexOptions.CultureInvariant);

        /// <summary>The line as the old client showed it: each link's text, without the markup around it.</summary>
        public static string Visible(string text)
            => string.IsNullOrEmpty(text) || text.IndexOf("Tell", System.StringComparison.Ordinal) < 0 ? text : Link.Replace(text, string.Empty);
    }
}
