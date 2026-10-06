using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Decal.Compat
{
    /// <summary>
    /// Points the calls a Decal plugin makes that this 64-bit .NET host cannot answer as the
    /// plugin expects at stand-ins in <c>Decal.Adapter.Hosting</c>, in the working copy the host
    /// runs it from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three kinds of call are redirected, each to a static method taking the same stack:
    /// </para>
    /// <list type="bullet">
    /// <item><c>Environment.GetFolderPath</c>, both forms, to <c>PluginFolders</c>: the player's
    /// own folders, or a stand-in for them when the host is given one;</item>
    /// <item><c>new XmlSerializer(type)</c> and <c>new XmlSerializer(type, defaultNamespace)</c>,
    /// a <c>newobj</c> of five bytes, to a call of the same five into <c>PluginXml</c>, which
    /// can serialize a framework collection of the plugin's own types where .NET's own cannot;</item>
    /// <item>a P/Invoke of Decal.dll's <c>DispatchOnChatCommand</c>, to <c>DecalNative</c>:
    /// there is no Decal.dll in this process.</item>
    /// </list>
    /// <para>
    /// The references are built from the plugin's own - its Environment, XmlSerializer, Type and
    /// string, and its reference to Decal.Adapter, which every Decal plugin has - so the rewritten
    /// plugin names no assembly it did not name before. An assembly that makes none of these
    /// calls is left as it is, byte for byte; one that cannot be read or written is too, and says
    /// why.
    /// </para>
    /// </remarks>
    internal static class CallRewrite
    {
        private const string Environment = "System.Environment";
        private const string XmlSerializer = "System.Xml.Serialization.XmlSerializer";
        private const string Hosting = "Decal.Adapter.Hosting";

        /// <summary>Decal.dll's entry points a stand-in answers, by name.</summary>
        private static readonly HashSet<string> DecalEntryPoints = new HashSet<string>(StringComparer.Ordinal) { "DispatchOnChatCommand" };

        /// <summary>
        /// The image with those calls redirected, or the image itself when it makes none or cannot
        /// be rewritten; <paramref name="problem"/> says why not in that case.
        /// </summary>
        /// <param name="redirected">How many calls were redirected: none when the image is returned as it was.</param>
        public static byte[] Rewrite(byte[] image, out int redirected, out string problem)
        {
            redirected = 0;
            problem = null;
            if (!RegistryRewrite.IsManaged(image))
                return image;

            try
            {
                using MemoryStream input = new MemoryStream(image, writable: false);
                using ModuleDefinition module = ModuleDefinition.ReadModule(input, new ReaderParameters { ReadSymbols = false, InMemory = true });
                bool callsDecal = module.GetTypes().Any(t => t.Methods.Any(IsDecalEntryPoint));
                if (!callsDecal && !module.GetMemberReferences().Any(m => IsFolderCall(m) || IsSerializerConstructor(m)))
                    return image;

                AssemblyNameReference adapter = module.AssemblyReferences.FirstOrDefault(a => a.Name == "Decal.Adapter");
                if (adapter == null)
                {
                    adapter = new AssemblyNameReference("Decal.Adapter", new Version(2, 9, 8, 3));
                    module.AssemblyReferences.Add(adapter);
                }

                foreach (TypeDefinition type in module.GetTypes())
                {
                    foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody))
                    {
                        foreach (Instruction instruction in method.Body.Instructions)
                        {
                            if (Redirect(module, adapter, instruction))
                                redirected++;
                        }
                    }
                }

                if (redirected == 0)
                    return image;

                using MemoryStream output = new MemoryStream();
                module.Write(output);
                return output.ToArray();
            }
            catch (Exception ex) when (WorkingCopy.MissingHostAssembly(ex) != null)
            {
                redirected = 0;
                problem = "its calls of the player's folders, the XML serializer and Decal.dll could not be pointed at this host's: " + WorkingCopy.MissingHostAssembly(ex);
                return image;
            }
            catch (Exception ex) when (ex is BadImageFormatException || ex is InvalidOperationException || ex is NotSupportedException
                                       || ex is ArgumentException || ex is IOException || ex is AssemblyResolutionException)
            {
                redirected = 0;
                problem = $"its calls of the player's folders, the XML serializer and Decal.dll could not be pointed at this host's ({ex.GetType().Name}: {ex.Message})";
                return image;
            }
        }

        private static bool Redirect(ModuleDefinition module, AssemblyNameReference adapter, Instruction instruction)
        {
            if (instruction.Operand is not MethodReference called)
                return false;

            if (instruction.OpCode == OpCodes.Call && IsFolderCall(called))
            {
                instruction.Operand = Same(new TypeReference(Hosting, "PluginFolders", module, adapter), "GetFolderPath", called);
                return true;
            }

            if (instruction.OpCode == OpCodes.Newobj && IsSerializerConstructor(called))
            {
                // The constructor's arguments and the object it leaves are a static's arguments and result.
                instruction.OpCode = OpCodes.Call;
                instruction.Operand = Same(new TypeReference(Hosting, "PluginXml", module, adapter), "Serializer", called, called.DeclaringType);
                return true;
            }

            // The plugin's own P/Invoke declaration, in its own module.
            if (instruction.OpCode == OpCodes.Call && called is MethodDefinition native && IsDecalEntryPoint(native))
            {
                instruction.Operand = Same(new TypeReference(Hosting, "DecalNative", module, adapter), native.PInvokeInfo.EntryPoint ?? native.Name, called);
                return true;
            }

            return false;
        }

        /// <summary>A static on the stand-in with the called method's parameters, and its return type unless given.</summary>
        private static MethodReference Same(TypeReference shim, string name, MethodReference called, TypeReference returns = null)
        {
            MethodReference method = new MethodReference(name, returns ?? called.ReturnType, shim) { HasThis = false };
            foreach (ParameterDefinition parameter in called.Parameters)
                method.Parameters.Add(new ParameterDefinition(parameter.ParameterType));
            return method;
        }

        private static bool IsFolderCall(MemberReference member)
            => member is MethodReference method && !method.HasThis && method.Name == "GetFolderPath"
               && method.DeclaringType.FullName == Environment && (method.Parameters.Count == 1 || method.Parameters.Count == 2);

        /// <summary>XmlSerializer's constructor from a type, or from a type and a default namespace.</summary>
        private static bool IsSerializerConstructor(MemberReference member)
        {
            if (member is not MethodReference method || method.Name != ".ctor" || method.DeclaringType.FullName != XmlSerializer
                || method.Parameters.Count == 0 || method.Parameters[0].ParameterType.FullName != "System.Type")
            {
                return false;
            }

            return method.Parameters.Count == 1
                   || (method.Parameters.Count == 2 && method.Parameters[1].ParameterType.MetadataType == MetadataType.String);
        }

        /// <summary>A P/Invoke of one of Decal.dll's entry points a stand-in answers.</summary>
        private static bool IsDecalEntryPoint(MethodDefinition method)
        {
            if (method == null || !method.IsPInvokeImpl || method.PInvokeInfo?.Module == null)
                return false;

            string library = method.PInvokeInfo.Module.Name;
            if (library.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                library = library.Substring(0, library.Length - 4);

            return string.Equals(library, "Decal", StringComparison.OrdinalIgnoreCase)
                   && DecalEntryPoints.Contains(method.PInvokeInfo.EntryPoint ?? method.Name);
        }
    }
}
