using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Decal.Compat
{
    /// <summary>
    /// Points a Decal plugin's reads of HKEY_LOCAL_MACHINE at <see cref="Decal.Adapter.Hosting.PluginRegistry"/>,
    /// in the working copy the host runs it from, so it sees the registry 32-bit Decal showed it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Five things are redirected, each to a static method taking the same stack:
    /// </para>
    /// <list type="bullet">
    /// <item><c>Registry.LocalMachine</c>, a field load, becomes a call of the same size;</item>
    /// <item><c>RegistryKey.OpenSubKey(name, writable)</c>, for reading only;</item>
    /// <item><c>RegistryKey.GetValue(name)</c> and <c>GetValue(name, default)</c>, which answer a
    /// plugin's own Path with its working copy;</item>
    /// <item><c>Registry.GetValue(key, name, default)</c>.</item>
    /// </list>
    /// <para>
    /// The references are built from the plugin's own: its RegistryKey, string and object, and its
    /// reference to Decal.Adapter - every Decal plugin has one - so the rewritten plugin names no
    /// assembly it did not name before. An assembly that does not touch the registry is left as it
    /// is, byte for byte; one that cannot be read or written is too, and says why.
    /// </para>
    /// </remarks>
    internal static class RegistryRewrite
    {
        private const string Registry = "Microsoft.Win32.Registry";
        private const string RegistryKey = "Microsoft.Win32.RegistryKey";
        private const string Shim = "Decal.Adapter.Hosting.PluginRegistry";

        /// <summary>
        /// The image with its registry reads redirected, or the image itself when it has none to
        /// redirect or cannot be rewritten; <paramref name="problem"/> says why not in that case.
        /// </summary>
        public static byte[] Rewrite(byte[] image, out bool changed, out string problem)
        {
            changed = false;
            problem = null;
            if (!IsManaged(image))
                return image;

            try
            {
                using MemoryStream input = new MemoryStream(image, writable: false);
                using ModuleDefinition module = ModuleDefinition.ReadModule(input, new ReaderParameters { ReadSymbols = false, InMemory = true });
                if (!module.GetTypeReferences().Any(t => t.FullName == Registry || t.FullName == RegistryKey))
                    return image;

                AssemblyNameReference adapter = module.AssemblyReferences.FirstOrDefault(a => a.Name == "Decal.Adapter");
                if (adapter == null)
                {
                    adapter = new AssemblyNameReference("Decal.Adapter", new Version(2, 9, 8, 3));
                    module.AssemblyReferences.Add(adapter);
                }

                TypeReference shim = new TypeReference("Decal.Adapter.Hosting", "PluginRegistry", module, adapter);
                foreach (TypeDefinition type in AllTypes(module))
                {
                    foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody))
                    {
                        foreach (Instruction instruction in method.Body.Instructions)
                            changed |= Redirect(module, shim, instruction);
                    }
                }

                if (!changed)
                    return image;

                using MemoryStream output = new MemoryStream();
                module.Write(output);
                return output.ToArray();
            }
            catch (Exception ex) when (ex is BadImageFormatException || ex is InvalidOperationException || ex is NotSupportedException
                                       || ex is ArgumentException || ex is IOException || ex is AssemblyResolutionException)
            {
                changed = false;
                problem = $"its registry reads could not be pointed at the 32-bit registry ({ex.GetType().Name}: {ex.Message})";
                return image;
            }
        }

        /// <summary>Whether an image is a .NET assembly: a native DLL - sqlite3.dll - is none of Cecil's business.</summary>
        internal static bool IsManaged(byte[] image)
        {
            try
            {
                using PEReader reader = new PEReader(new MemoryStream(image, writable: false));
                return reader.HasMetadata;
            }
            catch (BadImageFormatException)
            {
                return false;
            }
        }

        private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
        {
            Stack<TypeDefinition> pending = new Stack<TypeDefinition>(module.Types);
            while (pending.Count > 0)
            {
                TypeDefinition type = pending.Pop();
                yield return type;
                foreach (TypeDefinition nested in type.NestedTypes)
                    pending.Push(nested);
            }
        }

        private static bool Redirect(ModuleDefinition module, TypeReference shim, Instruction instruction)
        {
            // Registry.LocalMachine: ldsfld, five bytes, becomes a call of the same five.
            if (instruction.OpCode == OpCodes.Ldsfld && instruction.Operand is FieldReference field
                && field.DeclaringType.FullName == Registry && field.Name == "LocalMachine")
            {
                instruction.OpCode = OpCodes.Call;
                instruction.Operand = Static(shim, "LocalMachine", field.FieldType);
                return true;
            }

            if ((instruction.OpCode != OpCodes.Callvirt && instruction.OpCode != OpCodes.Call) || instruction.Operand is not MethodReference called)
                return false;

            string owner = called.DeclaringType.FullName;
            if (owner == RegistryKey && called.HasThis)
            {
                bool openForWriting = called.Name == "OpenSubKey" && called.Parameters.Count == 2
                                      && called.Parameters[1].ParameterType.MetadataType == MetadataType.Boolean;
                bool read = called.Name == "GetValue" && (called.Parameters.Count == 1 || called.Parameters.Count == 2);
                if (!openForWriting && !read)
                    return false;

                // The instance becomes the first argument of a static taking the same stack.
                MethodReference redirected = Static(shim, called.Name, called.ReturnType, called.DeclaringType);
                foreach (ParameterDefinition parameter in called.Parameters)
                    redirected.Parameters.Add(new ParameterDefinition(parameter.ParameterType));
                instruction.OpCode = OpCodes.Call;
                instruction.Operand = redirected;
                return true;
            }

            if (owner == Registry && !called.HasThis && called.Name == "GetValue" && called.Parameters.Count == 3)
            {
                MethodReference redirected = Static(shim, "GetValue", called.ReturnType);
                foreach (ParameterDefinition parameter in called.Parameters)
                    redirected.Parameters.Add(new ParameterDefinition(parameter.ParameterType));
                instruction.Operand = redirected;
                return true;
            }

            return false;
        }

        private static MethodReference Static(TypeReference shim, string name, TypeReference returns, TypeReference firstParameter = null)
        {
            MethodReference method = new MethodReference(name, returns, shim) { HasThis = false };
            if (firstParameter != null)
                method.Parameters.Add(new ParameterDefinition(firstParameter));
            return method;
        }
    }
}
