using System;
using System.IO;
using Backup.Core;

namespace Backup.Desktop
{
    internal enum PathPurpose
    {
        Download,
        CatalogImport,
        CleanupManifest,
        PrivateKey
    }

    internal sealed class PathHistory
    {
        private readonly RuntimeStateStore store;
        internal PathHistory(RuntimeStateStore value)
        {
            store = value;
        }

        private static string Key(PathPurpose purpose)
        {
            switch (purpose)
            {
                case PathPurpose.Download:
                    return "ui_last_download_directory_v1";
                case PathPurpose.CatalogImport:
                    return "ui_last_catalog_import_v1";
                case PathPurpose.CleanupManifest:
                    return "ui_last_cleanup_export_v1";
                case PathPurpose.PrivateKey:
                    return "ui_last_private_key_v1";
                default:
                    throw new ArgumentOutOfRangeException("purpose");
            }
        }

        internal static PathPurpose Purpose(string key)
        {
            foreach (PathPurpose value in Enum.GetValues(typeof(PathPurpose)))
                if (Key(value) == key)
                    return value;
            throw new ArgumentException("未登记的路径用途。", "key");
        }

        internal string Recall(PathPurpose purpose)
        {
            return store == null ? null : store.Get(Key(purpose));
        }

        internal void Remember(PathPurpose purpose, string path)
        {
            if (store != null && store.Get(Key(purpose)) != path)
                store.Set(Key(purpose), path);
        }

        internal string Download(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new IOException("请输入或选择目标路径。");
            string server;
            string normalized;
            if (RestoreDestination.TryServerRoot(path, out server))
                normalized = server;
            else
            {
                if (!Path.IsPathRooted(path))
                    throw new IOException("请输入或选择完整目标路径。");
                normalized = Path.GetFullPath(path);
            }

            // Existence and NAS-share checks belong to the common writer. Remembering
            // an accepted target must not add another synchronous network probe.
            Remember(PathPurpose.Download, normalized);
            return normalized;
        }
    }
}
