using System;
using System.Collections.Generic;
using System.IO;
using Backup.Core;

namespace Backup.Desktop
{
    public sealed partial class DesktopBackend
    {
#if UI_TEST
        public Action<string> TestMissingSourceProbe;
        public Action<string> TestMissingSourceDirectoryRead;
#endif
        private sealed class SourceDirectoryChecks
        {
            private enum DirectoryState { Present, Missing, Unavailable }

            private readonly DesktopBackend backend;
            private readonly Dictionary<string, DirectoryState> directories = new Dictionary<string, DirectoryState>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, HashSet<string>> listings = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            internal SourceDirectoryChecks(DesktopBackend owner)
            {
                backend = owner;
            }

            internal bool IsMissing(string path, string root)
            {
#if UI_TEST
                if (backend.TestMissingSourceProbe != null)
                    backend.TestMissingSourceProbe(path);
#endif
                if (string.IsNullOrWhiteSpace(root) || !Path.IsPathRooted(path))
                    return false;
                root = StoragePath.NormalizeRoot(root);
                string directory = Path.GetDirectoryName(Path.GetFullPath(path));
                if (directory == null)
                    return false;
                directory = StoragePath.NormalizeRoot(directory);
                if (!Under(directory, root) || ObserveDirectory(root, root) != DirectoryState.Present)
                    return false;
                var state = ObserveDirectory(directory, root);
                if (state == DirectoryState.Missing)
                    return true;
                if (state != DirectoryState.Present)
                    return false;
                var names = ReadNames(directory);
                return names != null && !names.Contains(Path.GetFileName(path));
            }

            private DirectoryState ObserveDirectory(string folder, string root)
            {
                DirectoryState state;
                if (!directories.TryGetValue(folder, out state))
                {
                    state = ProbeDirectory(folder, root);
                    directories.Add(folder, state);
                }
                return state;
            }

            private DirectoryState ProbeDirectory(string folder, string root)
            {
                string parent = string.Equals(folder, root, StringComparison.OrdinalIgnoreCase)
                    ? null : StoragePath.NormalizeRoot(Path.GetDirectoryName(folder));
                if (parent != null)
                {
                    var parentState = ObserveDirectory(parent, root);
                    if (parentState != DirectoryState.Present)
                        return parentState;
                    HashSet<string> names;
                    if (listings.TryGetValue(parent, out names))
                        if (names == null || !names.Contains(Path.GetFileName(folder)))
                            return names == null ? DirectoryState.Unavailable : DirectoryState.Missing;
                }

                try
                {
                    var attributes = File.GetAttributes(folder);
                    return (attributes & FileAttributes.Directory) != 0 && (attributes & FileAttributes.ReparsePoint) == 0
                        ? DirectoryState.Present : DirectoryState.Unavailable;
                }
                catch (IOException error)
                {
                    if (parent != null && (error is FileNotFoundException || error is DirectoryNotFoundException))
                    {
                        // A missing attribute lookup is conclusive only if a readable
                        // parent's complete listing also lacks the directory name.
                        var names = ReadNames(parent);
                        if (names != null && !names.Contains(Path.GetFileName(folder)))
                            return DirectoryState.Missing;
                    }
                    RuntimeDiagnostics.Record("Backup.Desktop/SourcePresence.cs:ProbeDirectory", error);
                }
                catch (UnauthorizedAccessException error)
                {
                    RuntimeDiagnostics.Record("Backup.Desktop/SourcePresence.cs:ProbeDirectory", error);
                }
                return DirectoryState.Unavailable;
            }

            private HashSet<string> ReadNames(string folder)
            {
                HashSet<string> names;
                if (listings.TryGetValue(folder, out names))
                    return names;
                try
                {
#if UI_TEST
                    if (backend.TestMissingSourceDirectoryRead != null)
                        backend.TestMissingSourceDirectoryRead(folder);
#endif
                    names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var control = backend.ActiveBackup;
                    foreach (string path in Directory.EnumerateFileSystemEntries(folder))
                    {
                        backend.OperationCancellation.ThrowIfCancellationRequested();
                        if (control != null)
                            control.Token.ThrowIfCancellationRequested();
                        if (backend.StopRequested)
                        {
                            names = null;
                            break;
                        }
                        names.Add(Path.GetFileName(path));
                    }
                }
                catch (IOException error)
                {
                    RuntimeDiagnostics.Record("Backup.Desktop/SourcePresence.cs:ReadNames", error);
                    names = null;
                }
                catch (UnauthorizedAccessException error)
                {
                    RuntimeDiagnostics.Record("Backup.Desktop/SourcePresence.cs:ReadNames", error);
                    names = null;
                }
                listings.Add(folder, names);
                return names;
            }
        }
    }
}
