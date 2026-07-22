using System;
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
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace Emby.Plugin.WeTrakr.Scrobbling
{
    /// <summary>
    /// Subscribes to ISessionManager playback events and dispatches them to the
    /// WeTrakr API. Emby instantiates and lifecycle-manages any IServerEntryPoint
    /// it finds in the plugin assembly, calling Run() at startup and Dispose() at
    /// shutdown.
    ///
    /// Event translation:
    ///   PlaybackStart   -> "PlaybackStart"
    ///   PlaybackStopped -> "PlaybackStop"
    ///   PlaybackProgress with IsPaused transition:
    ///       false -> true : "PlaybackPause"
    ///       true  -> false: "PlaybackUnpause"
    ///       no change     : "PlaybackProgress"
    ///
    /// Only Movie and Episode items are dispatched; everything else is ignored.
    /// Failures are logged and swallowed — scrobble must never break playback.
    /// </summary>
    public class ScrobbleManager : IServerEntryPoint
    {
        private readonly ISessionManager _sessions;
        private readonly IUserDataManager _userData;
        private readonly WeTrakrClient _client;
        private readonly PayloadBuilder _builder;
        private readonly PauseStateTracker _paused;
        private readonly FavoriteStateTracker _favorites;
        private readonly ILogger _logger;

        public ScrobbleManager(
            ISessionManager sessions,
            IUserDataManager userData,
            IHttpClient httpClient,
            ILogManager logManager)
        {
            _sessions = sessions;
            _userData = userData;
            _logger = logManager.GetLogger("WeTrakr");
            _client = new WeTrakrClient(httpClient, logManager);
            _builder = new PayloadBuilder();
            _paused = new PauseStateTracker();
            _favorites = new FavoriteStateTracker();
        }

        public void Run()
        {
            _sessions.PlaybackStart += OnPlaybackStart;
            _sessions.PlaybackProgress += OnPlaybackProgress;
            _sessions.PlaybackStopped += OnPlaybackStopped;
            _userData.UserDataSaved += OnUserDataSaved;
            _logger.Info("[WeTrakr] ScrobbleManager started — subscribed to playback + user-data events.");
        }

        public void Dispose()
        {
            _sessions.PlaybackStart -= OnPlaybackStart;
            _sessions.PlaybackProgress -= OnPlaybackProgress;
            _sessions.PlaybackStopped -= OnPlaybackStopped;
            _userData.UserDataSaved -= OnUserDataSaved;
            _logger.Info("[WeTrakr] ScrobbleManager stopped.");
        }

        private void OnPlaybackStart(object sender, PlaybackProgressEventArgs e)
        {
            _ = DispatchAsync(e, "PlaybackStart", e.IsPaused, played: false);
        }

        private void OnPlaybackStopped(object sender, PlaybackStopEventArgs e)
        {
            var key = SessionKey(e);
            _paused.Remove(key);
            _ = DispatchAsync(e, "PlaybackStop", isPaused: false, played: e.PlayedToCompletion);
        }

        private void OnPlaybackProgress(object sender, PlaybackProgressEventArgs e)
        {
            var key = SessionKey(e);
            var wasPaused = _paused.WasPaused(key);

            string eventName;
            if (e.IsPaused && !wasPaused) eventName = "PlaybackPause";
            else if (!e.IsPaused && wasPaused) eventName = "PlaybackUnpause";
            else eventName = "PlaybackProgress";

            _paused.Set(key, e.IsPaused);
            _ = DispatchAsync(e, eventName, e.IsPaused, played: false);
        }

        private async Task DispatchAsync(PlaybackProgressEventArgs e, string eventName, bool isPaused, bool played)
        {
            try
            {
                if (!ShouldDispatch(e.Item)) return;

                var session = e.Session;
                if (session == null) return;

                var config = Plugin.Instance?.Configuration;
                if (config == null) return;

                Guid userId;
                if (!Guid.TryParse(session.UserId, out userId)) return;

                var userConfig = FindUser(config, userId);
                if (userConfig == null) return; // this Emby user hasn't paired WeTrakr
                if (string.IsNullOrEmpty(userConfig.WebhookToken)) return; // not paired yet
                if (!userConfig.ScrobblePlaying) return; // user disabled

                var payload = _builder.Build(eventName, e.Item, session, e.PlaybackPositionTicks ?? 0, isPaused, played);
                await _client.SendAsync(config.ApiBaseUrl, userConfig, payload, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[WeTrakr] Dispatch failed for event {0}", ex, eventName);
            }
        }

        // --- UserData events (mark watched, toggle favorite, rating) ---

        private void OnUserDataSaved(object sender, UserDataSaveEventArgs e)
        {
            // Everything wrapped in try/catch — this handler runs on the HTTP
            // request thread of Emby's own FavoriteItems/PlayedItems endpoint;
            // if it throws, the original request 500s for the user.
            try
            {
                if (!ShouldDispatch(e.Item)) return;
                if (e.User == null || e.UserData == null) return;

                // Emby has no dedicated UserDataSaveReason for "favorite toggled" —
                // detect it as a transition instead, since UserDataSaved fires (with
                // the item's current IsFavorite value) on unrelated saves too, e.g.
                // every playback progress tick.
                var favoriteKey = e.User.Id.ToString("N") + ":" + e.Item.Id.ToString("N");
                var favoriteChanged = _favorites.HasChanged(favoriteKey, e.UserData.IsFavorite);

                string eventName;
                string saveReason;
                if (e.SaveReason == UserDataSaveReason.TogglePlayed)
                {
                    eventName = "ItemMarkedPlayed";
                    saveReason = e.SaveReason.ToString();
                }
                else if (e.SaveReason == UserDataSaveReason.UpdateUserRating)
                {
                    eventName = "UserDataSaved";
                    saveReason = e.SaveReason.ToString();
                }
                else if (favoriteChanged)
                {
                    eventName = "UserDataSaved";
                    saveReason = "ToggleFavorite";
                }
                else
                {
                    return;
                }

                _ = DispatchUserDataAsync(e, eventName, saveReason);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[WeTrakr] OnUserDataSaved sync path threw", ex);
            }
        }

        private async Task DispatchUserDataAsync(UserDataSaveEventArgs e, string eventName, string saveReason)
        {
            try
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null) return;
                if (e.User == null) return;

                var userConfig = FindUser(config, e.User.Id);
                if (userConfig == null) return; // this Emby user hasn't paired WeTrakr
                if (string.IsNullOrEmpty(userConfig.WebhookToken)) return;

                if (eventName == "ItemMarkedPlayed" && !userConfig.ScrobbleWatched) return;
                if (eventName == "UserDataSaved" && !userConfig.ScrobbleRatings) return;

                var payload = _builder.BuildUserData(eventName, e.Item, e.UserData, e.User, saveReason);
                await _client.SendAsync(config.ApiBaseUrl, userConfig, payload, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[WeTrakr] UserDataSaved dispatch failed for event {0}", ex, eventName);
            }
        }

        private static WeTrakrUserConfig FindUser(PluginConfiguration config, Guid userId)
        {
            return config.Users?.FirstOrDefault(u => u.UserId == userId);
        }

        private static bool ShouldDispatch(BaseItem item)
        {
            if (item == null) return false;
            return item is Movie || item is Episode;
        }

        private static string SessionKey(PlaybackProgressEventArgs e)
        {
            return e.PlaySessionId ?? e.Session?.Id ?? string.Empty;
        }
    }
}
