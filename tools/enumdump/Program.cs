using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

// Reads enum names and values out of the local ACE server's own assemblies, so
// protocol ids come from the server that answers them rather than from memory.
internal static class Program
{
    private static int Main(string[] args)
    {
        string directory = args.Length > 0 ? args[0] : @"C:\ACE\Server";
        string wanted = args.Length > 1 ? args[1] : "GameEventType";

        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            string candidate = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
        };

        foreach (string path in Directory.GetFiles(directory, "ACE.*.dll"))
        {
            Assembly assembly;
            try
            {
                assembly = Assembly.LoadFrom(path);
            }
            catch
            {
                continue;
            }

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).ToArray();
            }
            catch
            {
                continue;
            }

            foreach (Type type in types.Where(t => t.IsEnum && t.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase)))
            {
                Console.WriteLine($"=== {type.FullName} ({Path.GetFileName(path)}) ===");

                foreach (object value in Enum.GetValues(type))
                {
                    ulong numeric = Convert.ToUInt64(value);
                    Console.WriteLine($"0x{numeric:X4}  {value}");
                }
            }
        }

        return 0;
    }
}
