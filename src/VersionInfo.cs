using System.Reflection;

[assembly: AssemblyVersion(global::Backup.BuildVersion.Value)]
[assembly: AssemblyFileVersion(global::Backup.BuildVersion.Value)]

namespace Backup
{
    internal static class BuildVersion
    {
        // Change only this value for the next release; shared by the main app,
        // updater and built-in repository modules at compile time.
        internal const string Value = "1.1.6.0";
    }
}
