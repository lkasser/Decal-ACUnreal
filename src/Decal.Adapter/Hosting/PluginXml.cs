using System;
using System.Runtime.Loader;
using System.Xml.Serialization;

namespace Decal.Adapter.Hosting
{
    /// <summary>
    /// The XML serializer a Decal plugin makes for one of its own types. The host rewrites a
    /// registered plugin's working copy so that its <c>new XmlSerializer(type)</c> comes here
    /// (<c>Decal.Compat.CallRewrite</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// .NET's serializer writes code for the type it is given, into an assembly of its own made in
    /// the load context of the type's assembly - so that a plugin's serializer goes when the
    /// plugin's context is unloaded. That works for the plugin's own types, and fails for a
    /// framework collection of them: <c>List&lt;MyWorldObject&gt;</c>'s assembly is .NET's own, the
    /// code is written where nothing may refer to a plugin's types, and the constructor throws
    /// NotSupportedException, "A non-collectible assembly may not reference a collectible
    /// assembly". Mag-Tools' inventory logger makes exactly that serializer at every login.
    /// </para>
    /// <para>
    /// So a type whose own assembly is the framework's but which is made of a plugin's types is
    /// given a serializer whose code is written beside those types instead, through
    /// <see cref="XmlSerializer.FromMappings(XmlMapping[], Type)"/> anchored on the first of them.
    /// Every other type is made the usual way.
    /// </para>
    /// </remarks>
    public static class PluginXml
    {
        /// <summary><c>new XmlSerializer(type)</c>.</summary>
        public static XmlSerializer Serializer(Type type) => Serializer(type, null);

        /// <summary><c>new XmlSerializer(type, defaultNamespace)</c>.</summary>
        public static XmlSerializer Serializer(Type type, string defaultNamespace)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));

            Type anchor = IsCollectible(type) ? null : CollectiblePart(type);
            if (anchor == null)
                return new XmlSerializer(type, defaultNamespace);

            XmlTypeMapping mapping = new XmlReflectionImporter(defaultNamespace).ImportTypeMapping(type, null, defaultNamespace);
            return XmlSerializer.FromMappings(new XmlMapping[] { mapping }, anchor)[0];
        }

        /// <summary>The first type in a type's arguments or elements, however deep, whose assembly can be unloaded; null when none.</summary>
        public static Type CollectiblePart(Type type)
        {
            if (type.HasElementType)
            {
                Type element = type.GetElementType();
                return IsCollectible(element) ? element : CollectiblePart(element);
            }

            if (!type.IsGenericType)
                return null;

            foreach (Type argument in type.GetGenericArguments())
            {
                if (IsCollectible(argument))
                    return argument;

                Type deeper = CollectiblePart(argument);
                if (deeper != null)
                    return deeper;
            }

            return null;
        }

        private static bool IsCollectible(Type type)
            => !type.IsGenericParameter && AssemblyLoadContext.GetLoadContext(type.Assembly) is { IsCollectible: true };
    }
}
