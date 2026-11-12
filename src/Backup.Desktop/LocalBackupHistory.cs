using System.Collections.Generic;
using System.Linq;
using Backup.Core;

namespace Backup.Desktop
{
    public sealed partial class DesktopBackend
    {
        public IList<BackupNode> BackupNodesForBrowse(string taskId)
        {
            if(ActiveBackup==null)Catalog.RefreshLocalBackupNode(taskId);
            return Catalog.GetNodes(taskId).Concat(Catalog.GetLocalBackupNodes(taskId)).OrderByDescending(n=>n.CreatedUtc).ThenByDescending(n=>n.LocalOnly).ThenByDescending(n=>n.Id,System.StringComparer.Ordinal).ToList();
        }
        private BackupNode FindBrowseNode(string id)
        {return Catalog.GetLocalBackupNodes().FirstOrDefault(n=>n.Id==id)??Catalog.FindNode(id);}
    }
}
