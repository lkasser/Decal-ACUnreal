using System;
using System.IO;

namespace Decal.Adapter.Hosting
{
    /// <summary>
    /// The player's own folders - Documents, Application Data and the rest - as a Decal plugin
    /// asks for them. The host rewrites a registered plugin's working copy so that its calls of
    /// <c>Environment.GetFolderPath</c> come here (<c>Decal.Compat.CallRewrite</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Plugins keep what is the player's rather than the install's in Documents: Mag-Tools its
    /// settings, chat logs, inventory and its own error log in "Documents\Decal Plugins\Mag-Tools",
    /// where Decal's copy of it left them. So by default the answer is the real folder, and the
    /// plugin under this host carries on with the files it had under Decal.
    /// </para>
    /// <para>
    /// A host given a <see cref="Root"/> - Decal Compat's setting "UserFolders" - answers with a
    /// folder inside it instead, "Documents" for Documents, so a plugin run where the
    /// player's own files must not be touched, as in a test, finds an empty Documents of its own.
    /// </para>
    /// </remarks>
    public static class PluginFolders
    {
        /// <summary>
        /// The folder that stands in for all of the player's own, each special folder a folder
        /// inside it; null for the real ones. Set by the host.
        /// </summary>
        public static string Root { get; set; }

        /// <summary><c>Environment.GetFolderPath(folder)</c>.</summary>
        public static string GetFolderPath(Environment.SpecialFolder folder)
            => GetFolderPath(folder, Environment.SpecialFolderOption.None);

        /// <summary><c>Environment.GetFolderPath(folder, option)</c>.</summary>
        public static string GetFolderPath(Environment.SpecialFolder folder, Environment.SpecialFolderOption option)
        {
            string root = Root;
            if (string.IsNullOrEmpty(root))
                return Environment.GetFolderPath(folder, option);

            string path = Path.Combine(root, NameOf(folder));
            if (option != Environment.SpecialFolderOption.DoNotVerify)
                Directory.CreateDirectory(path);
            return path;
        }

        /// <summary>
        /// The folder inside <see cref="Root"/> that stands in for a special folder: "Documents" for
        /// the one Personal and MyDocuments both name, and the rest by their names.
        /// </summary>
        public static string NameOf(Environment.SpecialFolder folder)
            => folder == Environment.SpecialFolder.Personal ? "Documents" : folder.ToString();
    }
}
