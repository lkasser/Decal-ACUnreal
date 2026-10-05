using System.Reflection;

namespace Setup.Common
{
    /// <summary>The version a setup installs: its own, which the build sets to the product's.</summary>
    public static class SetupVersion
    {
        /// <summary>The assembly's informational version without the commit the SDK appends to it; "1.0.0", not "1.0.0+5abdedf".</summary>
        public static string Of(Assembly assembly)
        {
            string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? assembly.GetName().Version?.ToString()
                ?? "1.0.0";

            int plus = version.IndexOf('+');
            return plus < 0 ? version : version.Substring(0, plus);
        }
    }
}
