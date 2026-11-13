using System;

namespace Backup.Core
{
    public static class RemoteConnectionPolicy
    {
        public const int IdleSeconds = 600, ConnectSeconds = 60, OperationSeconds = 120, KeepAliveSeconds = 30;
        public const int UploadResponseSeconds = 360;
        public static bool SameCredentials(RemoteConnectionOptions left, RemoteConnectionOptions right)
        {
            return left.Username == right.Username && (left.Password ?? "") == (right.Password ?? "") && (left.AuthenticationMode ?? "password") == (right.AuthenticationMode ?? "password") &&
                (left.PrivateKeyPath ??
                "") == (right.PrivateKeyPath ??
                "") &&
                (left.PrivateKeyPassphrase ??
                "") == (right.PrivateKeyPassphrase ??
                "");
        }

        public static IManagedBackupDestination Create(string id, string protocol, string location, RemoteConnectionOptions options)
        {
            return RepositoryModules.Create(id, protocol, location, options);
        }
    }
}
