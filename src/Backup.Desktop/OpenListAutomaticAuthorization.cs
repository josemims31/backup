using System;
using System.Collections.Generic;
using System.Threading;
using Backup.Core;

namespace Backup.Desktop
{
    public sealed partial class DesktopBackend
    {
        private readonly HashSet<string> failedAutomaticOpenListAuthorizations = new HashSet<string>(StringComparer.Ordinal);

        private bool UseOpenListWebDavFallback(string location, RemoteConnectionOptions options)
        {
            if (!OpenListFallbackActive(location, options.Username))
                return false;
            RequestOpenListAuthorization(location, options, true);
            return OpenListFallbackActive(location, options.Username);
        }

        private bool TryAutomaticOpenListAuthorization(string location, RemoteConnectionOptions options, CancellationToken token)
        {
            string service = OpenListAuthorization.OpenListServiceAddress(location);
            if (options.OpenListUploadOptimization == false || options.OpenListRequiresTwoFactor == true ||
                !SameOpenListAccount(options, service, options.Username))
                return false;

            string key = OpenListSessionKey(service, options.Username + "\0" + options.OpenListToken);
            lock (authorizationFlightsGate)
                if (failedAutomaticOpenListAuthorizations.Contains(key))
                    return false;

            var candidate = new RemoteConnectionOptions
            {
                Username = options.Username,
                Password = options.Password,
                ServerFingerprint = options.ServerFingerprint,
                ServerTrust = options.ServerTrust,
                OpenListUploadOptimization = options.OpenListUploadOptimization,
                OpenListRequiresTwoFactor = options.OpenListRequiresTwoFactor,
                OpenListApiBase = options.OpenListApiBase,
                OpenListUsername = options.OpenListUsername,
                OpenListToken = options.OpenListToken
            };
            bool accepted = false;
            try
            {
                try
                {
                    if (!OpenListAuthorization.AuthorizeOpenList(location, candidate, null, token))
                        return false;
                }
                catch (OpenListAuthorizationException error)
                {
                    if (!error.TokenRejected || error.RequiresTwoFactor || error is OpenListLoginThrottledException)
                        throw;
                    // Only a rejected existing token permits one password login.
                    // The surrounding authorization flight is shared by all folders.
                    candidate.OpenListToken = "";
                    if (!OpenListAuthorization.AuthorizeOpenList(location, candidate, "", token))
                        return false;
                }

                token.ThrowIfCancellationRequested();
                options.OpenListApiBase = candidate.OpenListApiBase;
                options.OpenListUsername = candidate.OpenListUsername;
                options.OpenListToken = candidate.OpenListToken;
                options.OpenListRequiresTwoFactor = candidate.OpenListRequiresTwoFactor;
                accepted = true;
                return true;
            }
            catch (OpenListAuthorizationException error)
            {
                if (error.RequiresTwoFactor)
                {
                    options.OpenListRequiresTwoFactor = true;
                    var setting = SharedOpenListAuthorizationSetting(options);
                    if (setting.HasValue)
                        Catalog.SaveConfiguration(new[] { setting.Value });
                }
                RuntimeDiagnostics.Record("Backup.Desktop/OpenListAutomaticAuthorization.cs:TryAutomaticOpenListAuthorization", error);
                return false;
            }
            finally
            {
                if (!accepted && !token.IsCancellationRequested)
                    lock (authorizationFlightsGate)
                        failedAutomaticOpenListAuthorizations.Add(key);
            }
        }
    }
}
