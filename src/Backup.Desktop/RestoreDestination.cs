using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Backup.Desktop
{
    internal static class RestoreDestination
    {
        internal static bool TryServerRoot(string path, out string root)
        {
            root = null;
            if (string.IsNullOrWhiteSpace(path) || !path.StartsWith(@"\\", StringComparison.Ordinal))
                return false;
            string server = path.Substring(2).TrimEnd('\\');
            if (server.Length == 0 || server == "." || server == ".." || server.IndexOf('\\') >= 0 || server.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || server.EndsWith(".") ||
                server.EndsWith(" "))
                return false;
            root = @"\\" + server + "\\";
            return true;
        }

        internal static void Validate(string destination)
        {
            string server;
            if (TryServerRoot(destination, out server))
                return;
            bool unc = !string.IsNullOrWhiteSpace(destination) && destination.StartsWith(@"\\", StringComparison.Ordinal);
            bool absolute = unc 
                ? destination.Substring(2).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries).Length >= 2 
                : destination != null && destination.Length >= 3 && destination[1] == ':' &&
                (destination[2] == '\\' ||
                destination[2] == '/');
            if (!absolute || !Directory.Exists(destination))
                throw new IOException("请明确选择或进入一个有效的目标文件夹后再恢复。");
        }

        internal static string Boundary(string destination)
        {
            string server;
            return TryServerRoot(destination, out server) ? server : Path.GetFullPath(destination).TrimEnd('\\') + "\\";
        }

        internal static void CheckShares(string destination, IEnumerable<string> files, IEnumerable<string> directories = null)
        {
            string server;
            if (!TryServerRoot(destination, out server))
                return;
            var shares = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string relative in files)
            {
                string path = relative.Replace('/', '\\');
                if (path.IndexOf('\\') <= 0)
                    throw new IOException("NAS 根目录不能直接存放文件，请选择已有共享文件夹：" + relative);
                DesktopBackend.SafeRestorePath(server, path);
                shares.Add(path.Substring(0, path.IndexOf('\\')));
            }

            if (directories != null)
                foreach (string relative in directories.Where(path => !string.IsNullOrEmpty(path)))
                {
                    DesktopBackend.SafeRestorePath(server, relative);
                    shares.Add(relative.Replace('/', '\\').Split('\\')[0]);
                }

            foreach (string share in shares)
            {
                string path = server + share;
                if (!Directory.Exists(path))
                    throw new IOException("NAS 共享目录不存在或无访问权限，请先在 NAS 创建并开放该共享目录：" + path);
            }
        }

        internal static string ServerRelative(RestoreRequest request)
        {
            string original = RestorePathLayout.Original(request.File);
            if (original.StartsWith(@"\\", StringComparison.Ordinal))
            {
                var parts = original.Substring(2).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3)
                    return string.Join("\\", parts.Skip(1));
            }

            return !string.IsNullOrEmpty(request.File.EntryPath) ? request.File.EntryPath : request.RelativePath;
        }

        internal static string ParentBoundary(string file, string destination)
        {
            string server;
            return TryServerRoot(destination, out server) ? Path.GetPathRoot(file).TrimEnd('\\') : Path.GetFullPath(destination).TrimEnd('\\');
        }
    }
}
