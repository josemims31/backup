using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Backup.Updates
{
    public sealed class UpdateRollbackException : IOException
    {
        public UpdateRollbackException(string directory, Exception error) : base("更新未完成；部分文件回滚待处理，原文件保留在：" + directory, error)
        {
        }
    }

    public static class UpdateInstaller
    {
        private static void Replace(string source, string target, string transactionId)
        {
            UpdateProtocol.NoLinks(target);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            string temporary = target + ".update-" + transactionId + ".tmp";
            UpdateProtocol.NoLinks(temporary);
            try
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
                UpdateTransactions.CopyDurable(source, temporary);
                if (File.Exists(target))
                    File.Replace(temporary, target, null);
                else
                    File.Move(temporary, target);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }

        private static IEnumerable<string> PreviousPaths(string installation, InstallationManifest next)
        {
            string manifestPath = Path.Combine(installation, UpdateProtocol.ManifestName);
            if (File.Exists(manifestPath))
                return UpdateProtocol.ReadManifest(installation).Files.Select(file => UpdateProtocol.SafeRelativePath(file.Path)).ToList();
            // Bootstrap only known program files when updating an installation predating manifests.
            var result = new HashSet<string>(next.Files.Select(file => UpdateProtocol.SafeRelativePath(file.Path)), StringComparer.OrdinalIgnoreCase);
            foreach (string name in new[]
            {
                "Backup.exe",
                "Backup.Core.dll",
                "Backup.exe.config",
                "7za.dll",
                "Everything64.dll",
                "LICENSE-7ZIP.txt",
                "LICENSE-EVERYTHING.txt"
            }

            )
                result.Add(name);
            foreach (string name in new[]
            {
                "Renci.SshNet.dll",
                "BouncyCastle.Cryptography.dll",
                "Microsoft.Extensions.Logging.Abstractions.dll",
                "Microsoft.Extensions.DependencyInjection.Abstractions.dll",
                "System.Formats.Asn1.dll",
                "System.Buffers.dll",
                "System.Memory.dll",
                "System.ValueTuple.dll",
                "Microsoft.Bcl.AsyncInterfaces.dll",
                "System.Threading.Tasks.Extensions.dll",
                "System.Numerics.Vectors.dll",
                "System.Runtime.CompilerServices.Unsafe.dll",
                "Microsoft.Bcl.Cryptography.dll",
                "THIRD-PARTY-SFTP.txt"
            }

            )
                result.Add("repositories/sftp/" + name);
            return result;
        }

        private static void PruneEmptyParents(string installation, string path)
        {
            for (string parent = Path.GetDirectoryName(path); parent != null && !parent.Equals(installation, StringComparison.OrdinalIgnoreCase); parent = Path.GetDirectoryName(parent))
            {
                UpdateProtocol.NoLinks(parent);
                if (!Directory.Exists(parent) || Directory.EnumerateFileSystemEntries(parent).Any())
                    break;
                Directory.Delete(parent);
            }
        }

        public static string SessionDirectory(string installation, string payload)
        {
            installation = Path.GetFullPath(installation).TrimEnd('\\', '/');
            payload = Path.GetFullPath(payload);
            string updates = UpdateProtocol.UpdatesDirectory(installation), session = Path.GetDirectoryName(payload);
            Guid sessionId;
            if (!payload.StartsWith(updates + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(payload).Equals("payload", StringComparison.Ordinal) ||
                !Path.GetDirectoryName(session)
                .Equals(updates, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(session)
                .StartsWith("session-", StringComparison.Ordinal) ||
                !Guid.TryParseExact(Path.GetFileName(session)
                .Substring(8), "N", out sessionId))
                throw new IOException("更新暂存目录不属于当前安装目录。");
            UpdateProtocol.NoLinks(payload);
            return session;
        }

        public static void Install(string installation, string payload, string expectedVersion)
        {
            Install(new UpdateRequest { Installation = installation, Payload = payload, Version = expectedVersion, ExitAfterUpdate = true });
        }

        public static UpdateTransaction Install(UpdateRequest request)
        {
            string installation = Path.GetFullPath(request.Installation).TrimEnd('\\', '/'), payload = Path.GetFullPath(request.Payload);
            UpdateProtocol.NoLinks(installation);
            UpdateProtocol.NoLinks(payload);
            if (installation.Equals(Path.GetPathRoot(installation).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase) || !File.Exists(Path.Combine(installation, "Backup.exe")))
                throw new IOException("目标不是有效的软件安装目录。");
            string session = SessionDirectory(installation, payload);
            var manifest = UpdateProtocol.ReadManifest(payload);
            if (UpdateProtocol.ParseVersion(manifest.Version) != UpdateProtocol.ParseVersion(request.Version))
                throw new IOException("更新版本不一致。");
            UpdateProtocol.VerifyFiles(payload, manifest);
            var next = new HashSet<string>(manifest.Files.Select(file => UpdateProtocol.SafeRelativePath(file.Path)), StringComparer.OrdinalIgnoreCase);
            next.Add(UpdateProtocol.ManifestName);
            var previous = new HashSet<string>(PreviousPaths(installation, manifest), StringComparer.OrdinalIgnoreCase);
            previous.Add(UpdateProtocol.ManifestName);
            var all = new HashSet<string>(previous, StringComparer.OrdinalIgnoreCase);
            all.UnionWith(next);
            foreach (string relative in all)
            {
                if (!UpdateProtocol.ManagedPath(relative))
                    throw new IOException("软件文件清单越界。");
                UpdateProtocol.NoLinks(UpdateProtocol.FullPath(installation, relative));
            }

            string rollback = Path.Combine(session, "rollback");
            if (Directory.Exists(rollback) || File.Exists(Path.Combine(session, "transaction.json")))
                throw new IOException("本次更新已有回滚记录，已停止重复执行。");
            UpdateTransactions.RecoveryHelper(installation, payload);
            var transaction = new UpdateTransaction
            {
                Id = Guid.NewGuid().ToString("N"),
                Installation = installation,
                Session = session,
                Version = manifest.Version,
                State = UpdateTransactions.Preparing,
                Request = request
            };
            foreach (string relative in all)
            {
                string target = UpdateProtocol.FullPath(installation, relative);
                bool exists = File.Exists(target);
                transaction.Files.Add(new RollbackFile { Path = relative, Existed = exists, Size = exists ? new FileInfo(target).Length : 0, Sha256 = exists ? UpdateProtocol.Hash(target) : null });
            }

            // Persist the complete plan before touching any installed file. The snapshot is program-only.
            UpdateTransactions.Activate(transaction);
            try
            {
                Directory.CreateDirectory(rollback);
                foreach (var file in transaction.Files.Where(file => file.Existed))
                {
                    string backup = UpdateProtocol.FullPath(rollback, file.Path);
                    UpdateTransactions.CopyDurable(UpdateProtocol.FullPath(installation, file.Path), backup);
                    if (!UpdateTransactions.Matches(backup, file.Size, file.Sha256))
                        throw new IOException("原程序快照校验失败：" + file.Path);
                }

                transaction.State = UpdateTransactions.Installing;
                UpdateTransactions.Save(transaction);
                // Install the recovery-aware entry point first; the manifest is the final commit.
                foreach (var file in manifest.Files.OrderBy(file => file.Path.Equals("Backup.exe", StringComparison.OrdinalIgnoreCase) ? 0 : 1))
                {
                    string relative = UpdateProtocol.SafeRelativePath(file.Path);
                    Replace(UpdateProtocol.FullPath(payload, relative), UpdateProtocol.FullPath(installation, relative), transaction.Id);
                }

                foreach (string relative in previous.Where(path => !next.Contains(path)))
                {
                    string target = UpdateProtocol.FullPath(installation, relative);
                    if (File.Exists(target))
                        File.Delete(target);
                }

                Replace(Path.Combine(payload, UpdateProtocol.ManifestName), Path.Combine(installation, UpdateProtocol.ManifestName), transaction.Id);
                foreach (string relative in previous.Where(path => !next.Contains(path)))
                    PruneEmptyParents(installation, UpdateProtocol.FullPath(installation, relative));
                UpdateProtocol.VerifyFiles(installation, manifest);
                transaction.State = UpdateTransactions.AwaitingStartup;
                UpdateTransactions.Save(transaction);
                return transaction;
            }
            catch (Exception error)
            {
                try
                {
                    Rollback(transaction, error.Message);
                }
                catch (Exception restore)
                {
                    global::Backup.Updates.UpdateProtocol.Diagnostic("Backup.Updates/UpdateInstaller.cs:Install", restore);
                    throw new UpdateRollbackException(rollback, new AggregateException(error, restore));
                }

                throw new IOException("更新未完成，原程序文件已恢复：" + error.Message, error);
            }
        }

        public static void Rollback(UpdateTransaction transaction, string reason)
        {
            UpdateTransactions.Validate(transaction, transaction.Installation);
            bool untouched = transaction.State == UpdateTransactions.Preparing;
            if (transaction.State == UpdateTransactions.Confirmed)
                throw new IOException("已确认启动成功的更新不能自动回滚。");
            if (!untouched)
            {
                // Validate every required snapshot before restoring any file. Recovery is idempotent.
                foreach (var file in transaction.Files.Where(file => file.Existed))
                {
                    string backup = UpdateProtocol.FullPath(Path.Combine(transaction.Session, "rollback"), file.Path);
                    UpdateProtocol.NoLinks(backup);
                    if (!UpdateTransactions.Matches(backup, file.Size, file.Sha256))
                        throw new IOException("回滚快照缺失或校验失败：" + file.Path);
                }

                transaction.State = UpdateTransactions.RollingBack;
                transaction.Error = reason;
                UpdateTransactions.Save(transaction);
                try
                {
                    // Keep the new recovery-aware executable until all original dependencies are restored.
                    foreach (var file in transaction.Files.OrderBy(file => file.Path.Equals("Backup.exe", StringComparison.OrdinalIgnoreCase) ? 1 : 0))
                    {
                        string target = UpdateProtocol.FullPath(transaction.Installation, file.Path);
                        UpdateProtocol.NoLinks(target);
                        if (file.Existed)
                        {
                            if (!UpdateTransactions.Matches(target, file.Size, file.Sha256))
                                Replace(UpdateProtocol.FullPath(Path.Combine(transaction.Session, "rollback"), file.Path), target, transaction.Id);
                        }
                        else if (File.Exists(target))
                            File.Delete(target);
                        string temporary = target + ".update-" + transaction.Id + ".tmp";
                        UpdateProtocol.NoLinks(temporary);
                        if (File.Exists(temporary))
                            File.Delete(temporary);
                        if (!file.Existed)
                            PruneEmptyParents(transaction.Installation, target);
                    }
                }
                catch (global::System.Exception diagnosticFailure)
                {
                    global::Backup.Updates.UpdateProtocol.Diagnostic("Backup.Updates/UpdateInstaller.cs:Rollback", diagnosticFailure);
                    transaction.State = UpdateTransactions.RecoveryFailed;
                    UpdateTransactions.Save(transaction);
                    throw;
                }
            }

            transaction.State = UpdateTransactions.RolledBack;
            transaction.Error = reason;
            UpdateTransactions.Save(transaction);
            UpdateTransactions.Result(transaction, false, false, "更新未完成，原程序文件已恢复：" + reason);
            UpdateTransactions.Deactivate(transaction);
        }
    }
}
