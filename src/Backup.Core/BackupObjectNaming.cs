using System;
using System.Text.RegularExpressions;
namespace Backup.Core
{
    // Ownership policy explicitly includes matching foreign files. Match whole leaf
    // names only; a prefix, path, generic 7z file or arbitrary partial is not owned.
    public static class BackupObjectNaming
    {
        private static readonly Regex pattern=new Regex(@"\A(?<archive>(?:catalog|(?:node|data)(?:-[A-Za-z0-9]{4})?)-(?:[0-9]{8}|[0-9]{12}(?:-[1-9][0-9]*)?)\.7z)(?<volume>\.(?:00[1-9]|0[1-9][0-9]|[1-9][0-9]{2,}))?(?<temporary>\.upload-[0-9a-f]{32}\.partial|\.partial)?\z",RegexOptions.CultureInvariant|RegexOptions.IgnoreCase|RegexOptions.Compiled);
        public static bool IsManaged(string name){return name!=null&&pattern.IsMatch(name);}
        public static string ArchiveName(string name){var match=pattern.Match(name??"");return match.Success?match.Groups["archive"].Value:null;}
        public static bool IsTemporary(string name){var match=pattern.Match(name??"");return match.Success&&match.Groups["temporary"].Success;}
        public static string ObjectName(string name){var match=pattern.Match(name??"");return match.Success?match.Groups["archive"].Value+match.Groups["volume"].Value:null;}
    }
}
