using System.Runtime.CompilerServices;

// Built-in drivers share existing transport helpers. Third-party modules use
// the public SDK only and do not need friend access.
[assembly: InternalsVisibleTo("Backup.Repository.Local")]
[assembly: InternalsVisibleTo("Backup.Repository.Smb")]
[assembly: InternalsVisibleTo("Backup.Repository.WebDav")]
[assembly: InternalsVisibleTo("Backup.Repository.Sftp")]
