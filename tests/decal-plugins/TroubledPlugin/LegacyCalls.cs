using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Xml.Serialization;

namespace TroubledPlugin
{
    /// <summary>
    /// What Mag-Tools does that this 64-bit .NET host answers otherwise than Decal did: it finds
    /// the player's Documents, serializes a list of its own type, and offers a line to the
    /// plugins through Decal.dll. The plugin never calls these itself; the tests call them in the
    /// copy the host runs, to see what the host's rewrite of that copy made of them.
    /// </summary>
    public static class LegacyCalls
    {
        /// <summary>Where Mag-Tools keeps its settings and logs.</summary>
        public static string Documents() => Environment.GetFolderPath(Environment.SpecialFolder.Personal);

        /// <summary>
        /// A list of the plugin's own type through XmlSerializer and back, as Mag-Tools' inventory
        /// logger keeps the inventory: the name it went in with, when it comes back.
        /// </summary>
        public static string RoundTrip(string name)
        {
            XmlSerializer serializer = new XmlSerializer(typeof(List<Item>));
            using StringWriter writer = new StringWriter();
            serializer.Serialize(writer, new List<Item> { new Item { Name = name } });

            using StringReader reader = new StringReader(writer.ToString());
            return ((List<Item>)serializer.Deserialize(reader))[0].Name;
        }

        /// <summary>A line offered to the plugins as Mag-Tools' login and periodic commands are: bit 0 set when one ate it.</summary>
        public static int Dispatch(string line)
        {
            IntPtr text = Marshal.StringToBSTR(line);
            try
            {
                return DispatchOnChatCommand(ref text, 1);
            }
            finally
            {
                Marshal.FreeBSTR(text);
            }
        }

        [DllImport("Decal.dll")]
        private static extern int DispatchOnChatCommand(ref IntPtr text, [MarshalAs(UnmanagedType.U4)] int target);

        /// <summary>A line in the chat, as Virindi Chat System puts there the lines other plugins give it.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Say(string text) => Decal.Adapter.CoreManager.Current.Actions.AddChatText(text, 5);

        /// <summary>Another plugin's code run from this one's, as Mag-Tools gives its lines to Virindi Chat System.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Through(Action action) => action();

        /// <summary>One of the plugin's own types, as Mag-Tools' MyWorldObject is.</summary>
        public class Item
        {
            public string Name;
        }
    }
}
