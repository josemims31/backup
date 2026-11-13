using System;
using System.IO;
using System.Linq;

namespace Backup.Desktop
{
    public sealed partial class DesktopBackend
    {
#if UI_TEST
        public Action<string> TestMissingSourceProbe;
#endif
        private void ValidateSourceMissing(string path, string root)
        {
#if UI_TEST
            if (TestMissingSourceProbe != null)
                TestMissingSourceProbe(path);
#endif
            if (string.IsNullOrWhiteSpace(root) || !Under(path, root))
                throw new IOException("无法确认源目录，旧包保留：" + path);
            var attributes = File.GetAttributes(root);
            if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("源目录不可访问，旧包保留：" + root);
            bool missing = false;
            try
            {
                File.GetAttributes(path);
            }
            catch (FileNotFoundException)
            {
                missing = true;
            }
            catch (DirectoryNotFoundException)
            {
                missing = true;
            }

            if (!missing)
                throw new IOException("源文件已重新出现，旧包保留：" + path);
            // Attribute results alone can be cached by SMB. A successful, nonrecursive
            // directory read is also required before interpreting an absent path as deletion.
            for (string parent = Path.GetDirectoryName(path); !string.IsNullOrEmpty(parent) && Under(parent, root); parent = Path.GetDirectoryName(parent))
            {
                try
                {
                    attributes = File.GetAttributes(parent);
                    if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("源路径不可访问，旧包保留：" + parent);
                    using (var entries = Directory.EnumerateFileSystemEntries(parent).GetEnumerator())
                        entries.MoveNext();
                    return;
                }
                catch (FileNotFoundException diagnosticFailure)
                {
                    global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/SourcePresence.cs:ValidateSourceMissing", diagnosticFailure);
                }
                catch (DirectoryNotFoundException diagnosticFailure)
                {
                    global::Backup.Core.RuntimeDiagnostics.Record("Backup.Desktop/SourcePresence.cs:ValidateSourceMissing", diagnosticFailure);
                }
            }

            throw new IOException("无法联网确认源文件已删除，旧包保留：" + path);
        }
    }
}
