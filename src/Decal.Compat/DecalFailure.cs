using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Decal.Compat
{
    /// <summary>Why a Decal plugin did not start, in a few words and in full.</summary>
    internal sealed class DecalFailure
    {
        private DecalFailure(string status, string detail)
        {
            Status = status;
            Detail = detail;
        }

        /// <summary>A few words, for a list's status column.</summary>
        public string Status { get; }

        /// <summary>The whole reason, for the log, the tooltip and the line beneath the list.</summary>
        public string Detail { get; }

        /// <summary>Assemblies a Decal plugin may reference that this host does not have, and what each one is.</summary>
        private static readonly (string Prefix, string What)[] KnownAssemblies =
        {
            ("Decal.Interop.", "part of Decal's native (COM) layer, which this host does not have"),
            // Microsoft.DirectX itself, the maths, is a stand-in; the rest of it is not.
            ("Microsoft.DirectX.", "part of Managed DirectX other than its maths, a 32-bit .NET Framework library this host cannot load"),
            ("uTank2", "the real Virindi Tank, which this host replaces with its own"),
            ("VirindiHUDs", "the real Virindi HUDs, which this host replaces with its own"),
            ("VirindiHotkeySystem", "the real Virindi Hotkey System, which this host replaces with its own"),
            // Mag-Tools' inventory packer, auto buy-sell and auto trade-add; Item Tool's loot profiles.
            ("VTClassic", "Virindi Tank's loot profile reader from its folder, which this host's Virindi Tank keeps to itself"),
        };

        /// <summary>
        /// Reads an exception from loading or starting a plugin as a reason a player can act on:
        /// what the plugin needed, and why this host could not give it.
        /// </summary>
        /// <param name="ex">What was thrown, as caught; wrappers are looked through.</param>
        /// <param name="facts">The plugin's metadata, for the causes an exception does not name.</param>
        /// <param name="nativeProblems">Native DLLs its load context had to refuse, and why.</param>
        /// <param name="installDirectory">Its install folder, to look beside it for a native DLL it could not find.</param>
        public static DecalFailure Explain(Exception ex, PluginFacts facts, IReadOnlyList<string> nativeProblems, string installDirectory)
        {
            Exception cause = Unwrap(ex);
            string frame = PluginFrame(cause);
            string where = frame == null ? string.Empty : " It failed in " + frame + ".";

            switch (cause)
            {
                case FileNotFoundException missing when IsAssemblyLoad(missing) && AssemblyNameIn(missing.FileName ?? missing.Message) is string name:
                    return new DecalFailure("needs " + name, $"It needs the assembly {name}, {Describe(name)}.{where}");

                case FileLoadException unloadable when IsAssemblyLoad(unloadable) && AssemblyNameIn(unloadable.FileName ?? unloadable.Message) is string name:
                    return new DecalFailure("needs " + name, $"It needs the assembly {name}, which could not be loaded: {unloadable.Message}{where}");

                case BadImageFormatException image:
                    string file = image.FileName != null ? AssemblyNameIn(image.FileName) ?? Path.GetFileName(image.FileName) : null;
                    return new DecalFailure("32-bit " + (file ?? "code"),
                        $"{(file != null ? file + " is" : "Something it loads is")} built for 32-bit Windows only, which this 64-bit host cannot load.{where}");

                case DllNotFoundException native:
                    return Native(native, nativeProblems, installDirectory, where);

                case EntryPointNotFoundException entry:
                    return new DecalFailure("native call missing", $"It calls a native function that is not there: {entry.Message}{where}");

                case TypeLoadException type when TypeNameOf(type) is string typeName:
                    string from = AssemblyIn(type.Message);
                    return new DecalFailure("needs " + ShortType(typeName),
                        $"It uses {typeName}{(from != null ? " from " + from : string.Empty)}, which {(IsStandIn(from) ? "this host's stand-in does not have yet" : "is not there")}.{where}");

                case MissingMemberException member:
                    string wanted = MemberIn(member.Message);
                    return new DecalFailure("needs " + ShortMember(wanted),
                        $"It uses {wanted}, which {(IsStandIn(wanted) ? "this host's stand-in does not have yet" : "is not there")}.{where}");

                case NullReferenceException or ArgumentNullException when facts?.DecalKeyRead != null:
                    return new DecalFailure("reads 64-bit registry",
                        $"It stopped with {cause.GetType().Name}{(frame == null ? string.Empty : " in " + frame)}. It finds its folder by reading HKLM\\{facts.DecalKeyRead} through the registry's 64-bit view, "
                        + "where that key is not: Decal is 32-bit and wrote its keys under WOW6432Node. A 64-bit host cannot redirect a plugin's own registry reads, and that is the likely cause.");

                case PlatformNotSupportedException platform:
                    return new DecalFailure("not on .NET " + Environment.Version.Major, $"It uses something .NET {Environment.Version.Major} no longer has: {platform.Message}{where}");

                default:
                    return new DecalFailure(cause.GetType().Name, $"{cause.GetType().Name}: {cause.Message}{where}");
            }
        }

        /// <summary>
        /// Whether a line a plugin put in chat, or in a message box, reads as a report of its own
        /// failure - an exception's name or message - rather than as ordinary talk.
        /// </summary>
        public static bool LooksLikeFault(string text)
            => !string.IsNullOrEmpty(text) && Regex.IsMatch(text, @"Exception\b|Could not (load|find)|Unable to load DLL|Method not found|\bError\b");

        /// <summary>Whether a fault reads as a null where something was expected - what an unanswered registry read leaves.</summary>
        public static bool IsNullFault(string text)
            => !string.IsNullOrEmpty(text) && Regex.IsMatch(text, @"NullReferenceException|ArgumentNullException|Object reference not set");

        /// <summary>
        /// Reads an exception a plugin reported as text - in a message box, or in chat, having
        /// caught it itself - as a reason, the way <see cref="Explain"/> reads one it threw.
        /// </summary>
        public static string ExplainText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "nothing it said.";

            // Decal's chat links, "<Tell:IIDString:123:command>text<\Tell>", read as their text.
            text = AC.Host.Plugins.ChatMarkup.Visible(text);

            Match match;
            if ((match = Regex.Match(text, "Could not load file or assembly '([^',]+)")).Success)
                return $"it needs the assembly {match.Groups[1].Value}, {Describe(match.Groups[1].Value)}.";

            if ((match = Regex.Match(text, "Could not load type '([^']+)' from assembly '([^',]+)")).Success)
                return $"it uses {match.Groups[1].Value} from {match.Groups[2].Value}, which {(IsStandIn(match.Groups[2].Value) ? "this host's stand-in does not have yet" : "is not there")}.";

            if ((match = Regex.Match(text, "Method not found: '([^']+)'")).Success)
                return $"it uses {match.Groups[1].Value}, which {(IsStandIn(match.Groups[1].Value) ? "this host's stand-in does not have yet" : "is not there")}.";

            if ((match = Regex.Match(text, "Unable to load DLL '([^']+)'")).Success)
                return $"it needs the native {match.Groups[1].Value}, which could not be loaded - a 32-bit DLL, or none this host could find.";

            if (text.Contains("non-collectible assembly may not reference a collectible assembly", StringComparison.Ordinal))
                return "it builds code at run time over its own types - an XmlSerializer - which .NET cannot do for a plugin loaded so that it can be unloaded, as every Decal plugin here is.";

            if ((match = Regex.Match(text, "Access to the path '([^']+)' is denied")).Success)
                return $"it tried to write {match.Groups[1].Value}, which Windows does not let it{(text.Contains("exception handler", StringComparison.OrdinalIgnoreCase) ? " - while reporting an error of its own" : string.Empty)}.";

            string[] lines = text.Replace("\r", string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string first = lines.Length == 0 ? text.Trim() : lines[0];
            string at = lines.Skip(1).FirstOrDefault(l => l.StartsWith("at ", StringComparison.Ordinal));
            string said = first + (at != null ? " (" + at + ")" : string.Empty);
            return (said.Length > 240 ? said.Substring(0, 240) + "..." : said).TrimEnd('.') + ".";
        }

        /// <summary>A failure to load the assembly at all, before anything in it ran.</summary>
        public static DecalFailure ExplainLoad(Exception ex, PluginFacts facts, IReadOnlyList<string> nativeProblems, string installDirectory)
        {
            DecalFailure failure = Explain(ex, facts, nativeProblems, installDirectory);
            return new DecalFailure("failed to load: " + failure.Status, failure.Detail);
        }

        /// <summary>
        /// The exception worth reporting: through reflection's and a type initialiser's wrappers,
        /// to what actually went wrong.
        /// </summary>
        internal static Exception Unwrap(Exception ex)
        {
            Exception cause = ex;
            while (cause.InnerException != null && (cause is TargetInvocationException || cause is TypeInitializationException))
                cause = cause.InnerException;

            if (cause is ReflectionTypeLoadException types && types.LoaderExceptions.FirstOrDefault(e => e != null) is Exception first)
                cause = Unwrap(first);

            return cause;
        }

        private static DecalFailure Native(DllNotFoundException native, IReadOnlyList<string> nativeProblems, string installDirectory, string where)
        {
            string dll = Regex.Match(native.Message, "DLL '([^']+)'").Groups[1].Value;
            string problem = nativeProblems?.FirstOrDefault(p => dll.Length > 0 && p.StartsWith(Path.GetFileNameWithoutExtension(dll), StringComparison.OrdinalIgnoreCase))
                ?? nativeProblems?.FirstOrDefault();
            string file = dll.Length == 0 ? "a native DLL" : dll.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? dll : dll + ".dll";

            if (problem != null)
                return new DecalFailure("32-bit " + file, $"It needs {problem}.{where}");

            if (string.Equals(Path.GetFileNameWithoutExtension(file), "Decal", StringComparison.OrdinalIgnoreCase))
                return new DecalFailure("needs Decal.dll", $"It calls into Decal.dll, Decal's native core, which this host does not have.{where}");

            // Not beside it: Decal found such DLLs wherever the client's process could, and the
            // Virindi installers put theirs beside other plugins. Say what those are.
            string elsewhere = Sibling(installDirectory, file);
            string bits = elsewhere != null ? DecalPluginLoadContext.WrongMachine(elsewhere) : null;
            return new DecalFailure("needs " + file,
                $"It needs the native {file}, which is not beside it"
                + (elsewhere == null ? "." : bits != null ? $"; the copy at {elsewhere} is {bits}, which this host cannot load either." : $"; there is one at {elsewhere}, which Decal's process could find and this host does not look for.")
                + where);
        }

        /// <summary>A file of this name beside another plugin of the same install, if there is one.</summary>
        private static string Sibling(string installDirectory, string file)
        {
            try
            {
                string parent = string.IsNullOrEmpty(installDirectory) ? null : Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(installDirectory));
                if (parent == null || !Directory.Exists(parent))
                    return null;

                foreach (string folder in Directory.GetDirectories(parent))
                {
                    string candidate = Path.Combine(folder, file);
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
            }

            return null;
        }

        /// <summary>Where in the plugin's own code it went wrong, as "Type.Method"; null when that cannot be told.</summary>
        private static string PluginFrame(Exception cause)
        {
            string trace = cause.StackTrace;
            if (string.IsNullOrEmpty(trace))
                return null;

            foreach (string line in trace.Split('\n'))
            {
                string frame = line.Trim();
                if (!frame.StartsWith("at ", StringComparison.Ordinal))
                    continue;

                frame = frame.Substring(3);
                int arguments = frame.IndexOf('(');
                if (arguments > 0)
                    frame = frame.Substring(0, arguments);

                // The first frame not in the framework or the stand-ins is the plugin's.
                if (frame.StartsWith("System.", StringComparison.Ordinal) || frame.StartsWith("Microsoft.", StringComparison.Ordinal)
                    || frame.StartsWith("Decal.", StringComparison.Ordinal) || frame.StartsWith("VirindiViewService.", StringComparison.Ordinal)
                    || frame.StartsWith("AC.", StringComparison.Ordinal))
                    continue;

                return frame;
            }

            return null;
        }

        private static string Describe(string assembly)
        {
            foreach ((string prefix, string what) in KnownAssemblies)
            {
                if (assembly.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return what;
            }

            return "which is neither beside it nor another Decal plugin that is running";
        }

        private static bool IsStandIn(string name)
            => name != null && (name.StartsWith("Decal.Adapter", StringComparison.Ordinal) || name.StartsWith("VirindiViewService", StringComparison.Ordinal)
                || name.StartsWith("Decal.Filters", StringComparison.Ordinal) || name.StartsWith("Decal.FileService", StringComparison.Ordinal)
                || name.StartsWith("Decal.Interop.Core", StringComparison.Ordinal) || name.Contains(" Decal.Adapter.", StringComparison.Ordinal)
                || name.Contains(" VirindiViewService.", StringComparison.Ordinal) || name.Contains(" Decal.Filters.", StringComparison.Ordinal)
                || name.Contains(" Decal.Interop.Core.", StringComparison.Ordinal) || name == "Microsoft.DirectX"
                || name.StartsWith("VirindiHotkeySystem", StringComparison.Ordinal) || name.Contains(" VirindiHotkeySystem.", StringComparison.Ordinal)
                || name.StartsWith("VirindiHUDs", StringComparison.Ordinal) || name.Contains(" VirindiHUDs.", StringComparison.Ordinal)
                || name.StartsWith("uTank2", StringComparison.Ordinal) || name.Contains(" uTank2.", StringComparison.Ordinal));

        /// <summary>Whether a missing file is an assembly the runtime looked for, rather than a file of the plugin's own.</summary>
        private static bool IsAssemblyLoad(IOException ex)
            => (ex.Message ?? string.Empty).StartsWith("Could not load file or assembly", StringComparison.Ordinal);

        /// <summary>"Name" out of "Name, Version=..., Culture=..." or a message quoting it.</summary>
        private static string AssemblyNameIn(string text)
        {
            if (string.IsNullOrEmpty(text))
                return null;

            Match quoted = Regex.Match(text, "'([^',]+)(,[^']*)?'");
            string name = quoted.Success ? quoted.Groups[1].Value : text.Split(',')[0];
            name = name.Trim();
            if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                name = Path.GetFileNameWithoutExtension(name);
            return name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? null : name;
        }

        /// <summary>The type a TypeLoadException is about: its own field when the runtime filled it in, else its message's.</summary>
        private static string TypeNameOf(TypeLoadException ex)
        {
            if (!string.IsNullOrEmpty(ex.TypeName))
                return ex.TypeName;

            Match match = Regex.Match(ex.Message ?? string.Empty, "type '([^']+)'");
            return match.Success ? match.Groups[1].Value : null;
        }

        /// <summary>The assembly a TypeLoadException's message says the type was looked for in.</summary>
        private static string AssemblyIn(string message)
        {
            Match match = Regex.Match(message ?? string.Empty, "from assembly '([^',]+)");
            return match.Success ? match.Groups[1].Value : null;
        }

        /// <summary>"Void VirindiViewService.ACImage..ctor(System.Drawing.Bitmap)" out of "Method not found: '...'."</summary>
        private static string MemberIn(string message)
        {
            Match match = Regex.Match(message ?? string.Empty, "'([^']+)'");
            return match.Success ? match.Groups[1].Value : message;
        }

        private static string ShortType(string typeName)
        {
            string name = typeName.Split('+')[0];
            int dot = name.LastIndexOf('.');
            return (dot >= 0 ? name.Substring(dot + 1) : name) + (typeName.Contains('+') ? "+" + typeName.Split('+')[1] : string.Empty);
        }

        /// <summary>"ACImage(Bitmap)" out of "Void VirindiViewService.ACImage..ctor(System.Drawing.Bitmap)".</summary>
        private static string ShortMember(string member)
        {
            if (string.IsNullOrEmpty(member))
                return "a member";

            Match match = Regex.Match(member, @"([\w`]+)\.(\.ctor|[\w`]+)\(([^)]*)\)");
            if (!match.Success)
                return member.Length > 24 ? member.Substring(0, 24) : member;

            string parameters = string.Join(", ", match.Groups[3].Value.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim().Split('.').Last()));
            return match.Groups[2].Value == ".ctor"
                ? $"{match.Groups[1].Value}({parameters})"
                : $"{match.Groups[1].Value}.{match.Groups[2].Value}({parameters})";
        }
    }
}
