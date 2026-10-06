using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace Decal.Compat
{
    /// <summary>
    /// Widens the 32-bit address arithmetic of a Decal plugin's assemblies to 64 bits, in the
    /// working copy the host runs it from: an address taken as an int, an offset added, and the
    /// sum made an address again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The System.Data.SQLite that Virindi's tools ship - 1.0.61, from 2009, built when every
    /// Decal process was 32-bit - reads a blob that way, in <c>SQLite3.GetBytes</c> and
    /// <c>GetParamValueBytes</c>:
    /// <code>Marshal.Copy((IntPtr)(blob.ToInt32() + offset), buffer, start, count)</code>
    /// In this 64-bit process SQLite's memory lies above 2 GB, <c>IntPtr.ToInt32</c> throws
    /// OverflowException, and every blob read fails. Virindi Global Inventory keeps each item as a
    /// blob and swallows the failure, so its every search found nothing. Everything else that
    /// assembly does is pointer-sized already.
    /// </para>
    /// <para>
    /// The sequence - <c>IntPtr.ToInt32()</c>, an int argument, local or constant, <c>add</c> or
    /// <c>sub</c>, then <c>IntPtr</c>'s explicit conversion or constructor from an int - becomes
    /// <c>IntPtr.ToInt64()</c>, the same int widened, the same arithmetic, and the conversion or
    /// constructor from a long: what it always meant, done in 64 bits. Nothing else is touched -
    /// an int taken from a pointer for any other use stays as it was - and the references are the
    /// plugin's own IntPtr and long, so the rewritten assembly names nothing it did not before. An
    /// assembly with no such sequence is left as it is, byte for byte; one that cannot be read or
    /// written is too, and says why.
    /// </para>
    /// <para>
    /// Any registered plugin's copy of that System.Data.SQLite is mended so, whichever plugin
    /// brings it - Global Inventory, Chat System 5, Integrator2, Hotkey System - and any other
    /// assembly of a plugin's that does the same.
    /// </para>
    /// </remarks>
    internal static class PointerRewrite
    {
        private const string IntPtr = "System.IntPtr";

        /// <summary>
        /// The image with its 32-bit address arithmetic widened, or the image itself when it has
        /// none or cannot be rewritten; <paramref name="problem"/> says why not in that case.
        /// </summary>
        /// <param name="widened">How many sequences were widened: none when the image is returned as it was.</param>
        public static byte[] Rewrite(byte[] image, out int widened, out string problem)
        {
            widened = 0;
            problem = null;
            if (!RegistryRewrite.IsManaged(image))
                return image;

            try
            {
                using MemoryStream input = new MemoryStream(image, writable: false);
                using ModuleDefinition module = ModuleDefinition.ReadModule(input, new ReaderParameters { ReadSymbols = false, InMemory = true });
                if (!module.GetMemberReferences().Any(IsToInt32))
                    return image;

                foreach (TypeDefinition type in module.GetTypes())
                {
                    foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody))
                        widened += Widen(module, method);
                }

                if (widened == 0)
                    return image;

                using MemoryStream output = new MemoryStream();
                module.Write(output);
                return output.ToArray();
            }
            catch (Exception ex) when (WorkingCopy.MissingHostAssembly(ex) != null)
            {
                // Not the plugin's fault: an assembly of the host's own - Mono.Cecil.Rocks, as the
                // live install lacked - is not there to do the work.
                widened = 0;
                problem = "its 32-bit address arithmetic could not be widened for this 64-bit host: " + WorkingCopy.MissingHostAssembly(ex);
                return image;
            }
            catch (Exception ex) when (ex is BadImageFormatException || ex is InvalidOperationException || ex is NotSupportedException
                                       || ex is ArgumentException || ex is IOException || ex is AssemblyResolutionException)
            {
                widened = 0;
                problem = $"its 32-bit address arithmetic could not be widened for this 64-bit host ({ex.GetType().Name}: {ex.Message})";
                return image;
            }
        }

        /// <summary>Widens every sequence in one method; how many there were.</summary>
        private static int Widen(ModuleDefinition module, MethodDefinition method)
        {
            List<Instruction> found = new List<Instruction>();
            IList<Instruction> code = method.Body.Instructions;
            for (int i = 0; i + 3 < code.Count; i++)
            {
                if (IsToInt32(code[i].Operand as MemberReference) && code[i].OpCode == OpCodes.Call
                    && LoadsInt(method, code[i + 1])
                    && (code[i + 2].OpCode == OpCodes.Add || code[i + 2].OpCode == OpCodes.Sub)
                    && MakesIntPtrFromInt(code[i + 3]))
                {
                    found.Add(code[i]);
                }
            }

            if (found.Count == 0)
                return 0;

            // Long branches while the code grows, so that none falls out of a short one's reach.
            method.Body.SimplifyMacros();
            ILProcessor il = method.Body.GetILProcessor();
            foreach (Instruction toInt32 in found)
            {
                TypeReference intPtr = ((MethodReference)toInt32.Operand).DeclaringType;
                Instruction load = toInt32.Next;
                Instruction makes = load.Next.Next;

                toInt32.Operand = new MethodReference("ToInt64", module.TypeSystem.Int64, intPtr) { HasThis = true };
                il.InsertAfter(load, il.Create(OpCodes.Conv_I8));

                MethodReference from = (MethodReference)makes.Operand;
                MethodReference wide = new MethodReference(from.Name, from.ReturnType, intPtr) { HasThis = from.HasThis };
                wide.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int64));
                makes.Operand = wide;
            }

            method.Body.OptimizeMacros();
            return found.Count;
        }

        private static bool IsToInt32(MemberReference member)
            => member is MethodReference method && method.DeclaringType.FullName == IntPtr && method.Name == "ToInt32"
               && method.HasThis && method.Parameters.Count == 0;

        /// <summary><c>IntPtr</c>'s explicit conversion from an int, or its constructor from one.</summary>
        private static bool MakesIntPtrFromInt(Instruction instruction)
        {
            if (instruction.Operand is not MethodReference method || method.DeclaringType.FullName != IntPtr
                || method.Parameters.Count != 1 || method.Parameters[0].ParameterType.MetadataType != MetadataType.Int32)
            {
                return false;
            }

            return (instruction.OpCode == OpCodes.Call && method.Name == "op_Explicit" && !method.HasThis)
                   || (instruction.OpCode == OpCodes.Newobj && method.Name == ".ctor");
        }

        /// <summary>Whether an instruction pushes an int: a constant, or an argument or local declared as one.</summary>
        private static bool LoadsInt(MethodDefinition method, Instruction instruction)
        {
            switch (instruction.OpCode.Code)
            {
                case Code.Ldc_I4_M1:
                case Code.Ldc_I4_0:
                case Code.Ldc_I4_1:
                case Code.Ldc_I4_2:
                case Code.Ldc_I4_3:
                case Code.Ldc_I4_4:
                case Code.Ldc_I4_5:
                case Code.Ldc_I4_6:
                case Code.Ldc_I4_7:
                case Code.Ldc_I4_8:
                case Code.Ldc_I4_S:
                case Code.Ldc_I4:
                    return true;

                case Code.Ldarg_0:
                case Code.Ldarg_1:
                case Code.Ldarg_2:
                case Code.Ldarg_3:
                    return IsInt(Argument(method, instruction.OpCode.Code - Code.Ldarg_0)?.ParameterType);

                case Code.Ldarg_S:
                case Code.Ldarg:
                    return IsInt((instruction.Operand as ParameterDefinition)?.ParameterType);

                case Code.Ldloc_0:
                case Code.Ldloc_1:
                case Code.Ldloc_2:
                case Code.Ldloc_3:
                {
                    int index = instruction.OpCode.Code - Code.Ldloc_0;
                    return index < method.Body.Variables.Count && IsInt(method.Body.Variables[index].VariableType);
                }

                case Code.Ldloc_S:
                case Code.Ldloc:
                    return IsInt((instruction.Operand as VariableDefinition)?.VariableType);

                default:
                    return false;
            }
        }

        /// <summary>The argument an ldarg.N names: the instance first, where there is one.</summary>
        private static ParameterDefinition Argument(MethodDefinition method, int index)
        {
            if (method.HasThis)
            {
                if (index == 0)
                    return null;

                index--;
            }

            return index < method.Parameters.Count ? method.Parameters[index] : null;
        }

        private static bool IsInt(TypeReference type) => type != null && type.MetadataType == MetadataType.Int32;
    }
}
