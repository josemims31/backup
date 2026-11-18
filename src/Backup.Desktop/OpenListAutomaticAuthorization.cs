using System;
using System.Collections.Generic;
using System.Threading;
using Backup.Core;

namespace Backup.Desktop
{
    public sealed partial class DesktopBackend
    {
        internal static int AutomaticOpenListRetryMilliseconds = 30000;
        private readonly Dictionary<string, DateTime> automaticOpenListRetryTimes = new Dictionary<string, DateTime>(StringComparer.Ordinal);

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
            DateTime blockedUntil;
            lock (authorizationFlightsGate)
                if (automaticOpenListRetryTimes.TryGetValue(key, out blockedUntil) && DateTime.UtcNow < blockedUntil)
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
            DateTime retryAfter = DateTime.MaxValue;
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
            catch (OpenListTemporaryAuthorizationException)
            {
                retryAfter = DateTime.UtcNow.AddMilliseconds(AutomaticOpenListRetryMilliseconds);
                throw;
            }
            catch (OpenListAuthorizationException error)
            {
                if (error is OpenListLoginThrottledException)
                    retryAfter = DateTime.UtcNow.AddMinutes(5);
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
                lock (authorizationFlightsGate)
                {
                    if (accepted)
                        automaticOpenListRetryTimes.Remove(key);
                    else if (!token.IsCancellationRequested)
                        automaticOpenListRetryTimes[key] = retryAfter;
                }
            }
        }
    }
}
