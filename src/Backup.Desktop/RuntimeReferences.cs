using System;
using System.Collections.Generic;
using System.Linq;
using Backup.Core;

namespace Backup.Desktop
{
    public sealed partial class DesktopBackend
    {
        private static bool RemapKey(RemoteConnectionOptions options, IList<RuntimeFileMove> moves)
        {
            if (string.IsNullOrEmpty(options.PrivateKeyPath))
                return false;
            foreach (var move in moves.OrderByDescending(value => value.Source.Length))
            {
                if (options.PrivateKeyPath.Equals(move.Source, StringComparison.OrdinalIgnoreCase) ||
                    options.PrivateKeyPath.StartsWith(move.Source.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    options.PrivateKeyPath = move.Target + options.PrivateKeyPath.Substring(move.Source.Length);
                    return true;
                }
            }

            return false;
        }

        private static string RemapProtectedConnection(string value, IList<RuntimeFileMove> moves)
        {
            if (string.IsNullOrEmpty(value))
                return value;
            try
            {
                var options = UnprotectConnection(value);
                return RemapKey(options, moves) ? ProtectConnection(options) : value;
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                return value;
            }
            catch (FormatException)
            {
                return value;
            }
        }

        private void RemapRuntimeReferences(IList<RuntimeFileMove> moves)
        {
            foreach (var destination in Catalog.LoadTopology().Destinations.Where(value => value.Protocol == "sftp"))
            {
                try
                {
                    var options = LoadConnection(destination.Id);
                    if (RemapKey(options, moves))
                        SaveConnection(destination.Id, options);
                }
                catch (RemoteCredentialsUnavailableException)
                {
                    var reference = ConnectionReference(destination.Id);
                    if (RemapKey(reference, moves))
                        Catalog.SetSetting("connection_reference:" + destination.Id, EncodeConnectionReference(reference));
                }
            }

            foreach (var move in Catalog.GetWarehouseMoves().Where(value => value.State != "complete"))
            {
                string old = RemapProtectedConnection(move.OldConnection, moves), next = RemapProtectedConnection(move.NewConnection, moves);
                if (old != move.OldConnection || next != move.NewConnection)
                    Catalog.UpdateMoveConnections(move.DestinationId, old, next);
            }
        }
    }
}
