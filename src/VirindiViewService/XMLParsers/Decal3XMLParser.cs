using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml;
using AC.Host.Plugins.Views;

namespace VirindiViewService.XMLParsers
{
    public interface XMLParser
    {
        void ParseFromResource(string input, out ViewProperties properties, out ControlGroup controls);

        void Parse(string input, out ViewProperties properties, out ControlGroup controls);

        void Parse(XmlDocument input, out ViewProperties properties, out ControlGroup controls);
    }

    /// <summary>
    /// Reads Decal 3 view XML into VVS's view properties and controls - by way of the host's
    /// own parser, so a view reads the same whichever of the two a plugin chose.
    /// </summary>
    public class Decal3XMLParser : XMLParser
    {
        public Decal3XMLParser()
        {
        }

        public void Parse(string input, out ViewProperties properties, out ControlGroup controls)
        {
            DecalView view = DecalView.Parse(input);
            properties = new ViewProperties(view.Title, view.Width, view.Height, ACImage.FromImageKey(view.IconKey));
            controls = new ControlGroup(view);
        }

        public void Parse(XmlDocument input, out ViewProperties properties, out ControlGroup controls)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            Parse(input.OuterXml, out properties, out controls);
        }

        /// <summary>
        /// Parses a view embedded as a resource in the calling plugin. VVS was never told which
        /// plugin was asking, so it looked in the caller's assembly; so does this, and then in
        /// every assembly loaded, since a plugin's view code sometimes lives in a library of its own.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ParseFromResource(string input, out ViewProperties properties, out ControlGroup controls)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));

            Assembly source = Assembly.GetCallingAssembly();
            if (source.GetManifestResourceInfo(input) == null)
                source = FindResource(input) ?? throw new ArgumentException($"No loaded assembly has an embedded resource '{input}'.", nameof(input));

            using Stream stream = source.GetManifestResourceStream(input);
            using StreamReader reader = new StreamReader(stream);
            Parse(reader.ReadToEnd(), out properties, out controls);
        }

        private static Assembly FindResource(string name)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic)
                    continue;

                try
                {
                    if (assembly.GetManifestResourceInfo(name) != null)
                        return assembly;
                }
                catch (NotSupportedException)
                {
                    // An assembly that cannot list its resources has none for us.
                }
            }

            return null;
        }
    }
}
