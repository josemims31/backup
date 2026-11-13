using Backup.Core;

namespace Backup.Desktop
{
    public sealed partial class DesktopBackend
    {
        private NodeFileDatabase RecoverMovedNodeCache(BackupNode node)
        {
            return LocalNodes.Recover(node);
        }
    }
}
