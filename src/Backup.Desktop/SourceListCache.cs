using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Backup.Core;

namespace Backup.Desktop
{
    public sealed class SourceListEntry
    {
        public string Path, ArchivePath;
        public long Size, Modified;
    }

    public sealed class SourceListSnapshot
    {
        public string Root;
        public List<SourceListEntry> Files = new List<SourceListEntry>();
    }

    public sealed partial class DesktopBackend
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, LoadedSource> sourceSnapshots = new System.Collections.Concurrent.ConcurrentDictionary<string, LoadedSource>();
        internal bool TrySourceSnapshot(string id, out LoadedSource source)
        {
            return sourceSnapshots.TryGetValue(id, out source);
        }

        internal void ForgetSourceSnapshot(string id)
        {
            LoadedSource ignored;
            sourceSnapshots.TryRemove(id, out ignored);
        }

        public void SaveFileList(string id, LoadedSource source, LoadedSource previous = null)
        {
            foreach (var file in source.Files)
                file.ArchivePath = Display.ArchivePath(file.Path);
            Catalog.SaveSourceCache(id, source.Root, source.Files, previous == null ? null : new CachedSourceList { Root = previous.Root, Files = previous.Files });
            sourceSnapshots[id] = source;
            if (repositoryRuntime != null)
                repositoryRuntime.Notify(CatalogChange.Sources, new[] { id });
        }

        public LoadedSource ReadFileList(SourceDefinition source)
        {
            var snapshot = Catalog.ReadSourceCache(source.Id, source.Location);
            if (snapshot == null)
                return null;
            var value = new LoadedSource
            {
                Root = snapshot.Root,
                Files = snapshot.Files
            };
            sourceSnapshots[source.Id] = value;
            return value;
        }

        private void MigrateSourceCaches()
        {
            var legacy = Catalog.LegacySourceCaches();
            foreach (var pair in legacy)
                try
                {
                    var snapshot = JsonSettings.Decode(pair.Value, new SourceListSnapshot());
                    if (snapshot.Files == null || string.IsNullOrEmpty(snapshot.Root))
                        continue;
                    SaveFileList(
                        pair.Key.Substring("source_files_v1:".Length),
                        new LoadedSource {
                        Root = snapshot.Root,
                        Files = snapshot.Files.Select(file => new SourceItem {
                        Path = file.Path,
                        ArchivePath = file.ArchivePath,
                        Size = file.Size,
                        Modified = file.Modified,
                        VerifyContentDuringRead = false
                    })
                        .ToList()
                    }
                    );
                }
                catch (System.Runtime.Serialization.SerializationException)
                {
                    RuntimeLog.Append(BaseDirectory, "ui-errors.log", "旧文件列表缓存无法读取，将重新查询 Everything。");
                }

            if (legacy.Count > 0)
                Catalog.RemoveLegacySourceCaches();
            Catalog.CompactAfterCacheMigration(legacy.Count > 0);
        }

        internal static bool SameFileList(LoadedSource prior, string root, IList<SourceItem> files)
        {
            if (prior == null || !string.Equals(prior.Root, root, StringComparison.OrdinalIgnoreCase) || prior.Files.Count != files.Count)
                return false;
            var lookup = prior.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                SourceItem old;
                if (!lookup.TryGetValue(file.Path, out old) || !SourceItem.SameMetadata(file, old))
                    return false;
            }

            return true;
        }
    }

    public sealed partial class MainWindowController
    {
        private async Task InitializeFileListsAsync()
        {
            SetOperationText("正在加载上次的文件列表");
            var sources = Workspace.Sources.Where(source => !source.Deleted).ToList();
            var previous = await Task.Run(() => sources.Select(source => new { source.Id, Value = Backend.ReadFileList(source) }).Where(item => item.Value != null).ToList());
            if (disposed)
                return;
            foreach (var item in previous)
                if (!loaded.ContainsKey(item.Id) &&
                    Workspace.Sources.Any(source => !source.Deleted && source.Id == item.Id && source.Location.Equals(item.Value.Root, StringComparison.OrdinalIgnoreCase)))
                    loaded[item.Id] = item.Value;
            if (previous.Count > 0)
                await PrepareCurrentTreeAsync();
            await RefreshAllFileListsAsync();
        }
    }
}
