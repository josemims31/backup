using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Backup.Core
{
    // Shared by the warehouse and its folder views. Only the configured warehouse
    // root is protected as an existing repository; missing child folders may still
    // be restored. An unconfirmed creation at any level is never submitted twice.
    internal sealed class WebDavDirectorySafety
    {
        private readonly object gate=new object();private readonly string destination;
        internal Uri RepositoryRoot{get;private set;}
        private BackupCatalog catalog;private bool known,loaded;private DateTime checkedUtc;
        private readonly HashSet<string> pending=new HashSet<string>(StringComparer.Ordinal);
        internal WebDavDirectorySafety(string id,Uri root){destination=id;RepositoryRoot=root;}
        internal void Attach(BackupCatalog value)
        {
            if(value==null)return;lock(gate){if(ReferenceEquals(catalog,value))return;catalog=value;loaded=false;string configured=value.WebDavRepositoryLocation(destination);if(configured!=null){var location=RemotePath.Root(configured,"http");if(RepositoryRoot.Scheme==location.Scheme&&RepositoryRoot.Authority==location.Authority&&RepositoryRoot.AbsolutePath.StartsWith(location.AbsolutePath,StringComparison.Ordinal))RepositoryRoot=location;}}
        }
        private void LoadUnsafe(){if(loaded)return;known=known||catalog!=null&&catalog.WebDavRepositoryWasUsed(destination,RepositoryRoot.AbsoluteUri);if(known&&catalog!=null)catalog.RememberWebDavRepository(destination,RepositoryRoot.AbsoluteUri);loaded=true;}
        internal bool Known{get{lock(gate){LoadUnsafe();return known;}}}
        internal bool NeedsRootCheck{get{lock(gate){LoadUnsafe();return known&&DateTime.UtcNow-checkedUtc>=TimeSpan.FromSeconds(15);}}}
        internal bool IsRepositoryRoot(Uri location){return string.Equals(location.AbsoluteUri,RepositoryRoot.AbsoluteUri,StringComparison.Ordinal);}
        internal void ObserveFolder(Uri location)
        {
            lock(gate){if(location.Scheme==RepositoryRoot.Scheme&&location.Authority==RepositoryRoot.Authority&&location.AbsolutePath.StartsWith(RepositoryRoot.AbsolutePath,StringComparison.Ordinal)){
                    LoadUnsafe();if(!known){if(catalog!=null)catalog.RememberWebDavRepository(destination,RepositoryRoot.AbsoluteUri);known=true;}checkedUtc=DateTime.UtcNow;
                }
                bool submitted=pending.Remove(location.AbsoluteUri);if(catalog!=null&&(submitted||catalog.WebDavDirectoryCreationPending(destination,location.AbsoluteUri)))catalog.ForgetWebDavDirectoryCreation(destination,location.AbsoluteUri);
            }
        }
        internal bool TryCreate(Uri location)
        {
            lock(gate){LoadUnsafe();if(IsRepositoryRoot(location)&&known||pending.Contains(location.AbsoluteUri))return false;
                if(catalog!=null&&!catalog.ClaimWebDavDirectoryCreation(destination,location.AbsoluteUri))return false;
                pending.Add(location.AbsoluteUri);return true;
            }
        }
        internal void RejectCreation(Uri location){lock(gate){pending.Remove(location.AbsoluteUri);if(catalog!=null)catalog.ForgetWebDavDirectoryCreation(destination,location.AbsoluteUri);}}
        internal bool CreationPending(Uri location){lock(gate)return pending.Contains(location.AbsoluteUri)||catalog!=null&&catalog.WebDavDirectoryCreationPending(destination,location.AbsoluteUri);}
    }
}
