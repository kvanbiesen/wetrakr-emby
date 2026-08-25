using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugin.WeTrakr.Api;
using Emby.Plugin.WeTrakr.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace Emby.Plugin.WeTrakr.Scrobbling
{
    /// <summary>
    /// Pushes each paired user's already-watched Movies/Episodes to WeTrakr as
    /// "ItemMarkedPlayed" events (same event the live UserDataSaved handler sends —
    /// this is a bulk backfill of pre-existing watched status, not a new event type).
    ///
    /// This is the Emby -> WeTrakr direction only. There is no admin-configured
    /// default trigger (matches Trakt's own scheduled tasks) — set a recurring
    /// schedule from Dashboard -> Scheduled Tasks if you want this to run
    /// automatically, or run it on demand.
    ///
    /// No bulk API exists on WeTrakr's side yet (confirmed 2026-07: the "Jellyfin
    /// history import" feature on wetrakr.com is itself still "coming soon"), so
    /// this sends one webhook POST per watched item through the same endpoint live
    /// scrobbles use. WeTrakr's device-code/pairing endpoints have shown aggressive
    /// rate limiting under light testing, so sends are deliberately throttled — a
    /// large library will take a while. Revisit this once/if WeTrakr exposes a real
    /// bulk history-import endpoint.
    /// </summary>
    public class SyncToWeTrakrTask : IScheduledTask
    {
        private static readonly TimeSpan ThrottleDelay = TimeSpan.FromMilliseconds(400);

        private readonly IUserManager _userManager;
        private readonly ILibraryManager _libraryManager;
        private readonly IUserDataManager _userDataManager;
        private readonly WeTrakrClient _client;
        private readonly PayloadBuilder _builder;
        private readonly ILogger _logger;

        public SyncToWeTrakrTask(
            IUserManager userManager,
            ILibraryManager libraryManager,
            IUserDataManager userDataManager,
            IHttpClient httpClient,
            ILogManager logManager)
        {
            _userManager = userManager;
            _libraryManager = libraryManager;
            _userDataManager = userDataManager;
            _logger = logManager.GetLogger("WeTrakr");
            _client = new WeTrakrClient(httpClient, logManager);
            _builder = new PayloadBuilder();
        }

        public string Key => "WeTrakrSyncWatchedHistory";

        public string Name => "Sync WeTrakr watched history";

        public string Category => "WeTrakr";

        public string Description => "Pushes each connected user's already-watched movies and episodes to WeTrakr.";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new List<TaskTriggerInfo>();
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null || config.Users.Length == 0)
            {
                _logger.Info("[WeTrakr] SyncToWeTrakrTask: no paired users, nothing to do.");
                return;
            }

            var eligibleUsers = config.Users
                .Where(u => !string.IsNullOrEmpty(u.WebhookToken) && u.SyncWatchedHistory)
                .ToList();

            if (eligibleUsers.Count == 0)
            {
                _logger.Info("[WeTrakr] SyncToWeTrakrTask: no users have watched-history sync enabled.");
                return;
            }

            var percentPerUser = 100.0 / eligibleUsers.Count;
            var currentProgress = 0.0;

            foreach (var userConfig in eligibleUsers)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var user = _userManager.GetUserById(userConfig.UserId);
                if (user == null)
                {
                    _logger.Warn("[WeTrakr] SyncToWeTrakrTask: paired user {0} no longer exists on this server, skipping.", userConfig.UserId);
                    currentProgress += percentPerUser;
                    progress.Report(currentProgress);
                    continue;
                }

                await SyncUserWatchedHistory(user, userConfig, config.ApiBaseUrl, cancellationToken).ConfigureAwait(false);

                currentProgress += percentPerUser;
                progress.Report(currentProgress);
            }
        }

        private async Task SyncUserWatchedHistory(User user, WeTrakrUserConfig userConfig, string apiBaseUrl, CancellationToken cancellationToken)
        {
            var items = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { typeof(Movie).Name, typeof(Episode).Name },
                IsVirtualItem = false
            });

            var excluded = userConfig.LocationsExcluded ?? Array.Empty<string>();
            var sent = 0;

            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Token may have been cleared mid-loop if WeTrakr rejected it (401/403) —
                // stop hammering the API for this user once that happens.
                if (string.IsNullOrEmpty(userConfig.WebhookToken)) break;

                if (IsExcluded(item, excluded)) continue;

                var userData = _userDataManager.GetUserData(user, item);
                if (userData == null || !userData.Played) continue;

                var payload = _builder.BuildUserData("ItemMarkedPlayed", item, userData, user, "ScheduledSync");
                await _client.SendAsync(apiBaseUrl, userConfig, payload, cancellationToken).ConfigureAwait(false);
                sent++;

                await Task.Delay(ThrottleDelay, cancellationToken).ConfigureAwait(false);
            }

            _logger.Info("[WeTrakr] SyncToWeTrakrTask: sent {0} watched items for user {1}.", sent, user.Name);
        }

        private static bool IsExcluded(BaseItem item, string[] excludedLocations)
        {
            if (excludedLocations.Length == 0 || string.IsNullOrEmpty(item.Path)) return false;

            foreach (var location in excludedLocations)
            {
                if (!string.IsNullOrEmpty(location) && item.Path.StartsWith(location, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
