using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugin.WeTrakr.Api;
using Emby.Plugin.WeTrakr.Configuration;
using MediaBrowser.Common;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace Emby.Plugin.WeTrakr.Services
{
    /// <summary>
    /// Mirrors a movie's favorite toggle in Emby to WeTrakr, live, the same way WatchedMarks mirrors
    /// "mark as played". Movies only: WeTrakr addresses a favorited show or episode by its own internal
    /// id, which Emby has no way to know, so only movies (by tmdb/imdb/tvdb, like everywhere else in
    /// this plugin) are sent. The other direction, WeTrakr's favorites into Emby, is read during a sync
    /// run (see HistorySync) rather than here, since there is no Emby-side event to react to. Off by
    /// default; a user turns it on from their WeTrakr settings page.
    /// </summary>
    public class FavoritesSync : IServerEntryPoint
    {
        private readonly IUserDataManager _userData;
        private readonly IUserManager _users;
        private readonly ILibraryManager _library;
        private readonly WeTrakrApi _api;
        private readonly ILogger _logger;

        // a generic last-value-seen gate; reused here for the favorite flag instead of played
        private readonly PlayedStateTracker _state = new PlayedStateTracker();

        public FavoritesSync(IUserDataManager userData, IUserManager users, ILibraryManager library, IHttpClient http, ILogManager logManager, IApplicationHost host)
        {
            _userData = userData;
            _users = users;
            _library = library;
            _logger = logManager.GetLogger("WeTrakr Favorites");
            _api = new WeTrakrApi(http, users, logManager, host);
        }

        // See the note on Scrobbler.Run(): this runs during Emby's own startup sequence and must
        // never throw into it, on any server version.
        public void Run()
        {
            try
            {
                _userData.UserDataSaved += OnUserDataSaved;
            }
            catch (Exception ex)
            {
                _logger.ErrorException("WeTrakr favorites mirroring failed to start", ex);
            }
        }

        public void Dispose()
        {
            try
            {
                _userData.UserDataSaved -= OnUserDataSaved;
            }
            catch (Exception ex)
            {
                _logger.ErrorException("WeTrakr favorites mirroring failed to stop cleanly", ex);
            }
        }

        // raised inline by Emby's own save: it has to stay quick and must never throw. Emby saves a
        // favorite toggle under the same reason as a star rating (there is no reason of its own), so
        // a rating-only change also lands here and is dropped below once the favorite flag has not moved.
        private void OnUserDataSaved(object sender, UserDataSaveEventArgs e)
        {
            try
            {
                if (e.SaveReason != UserDataSaveReason.UpdateUserRating || e.User == null || e.UserData == null) return;
                var movie = e.Item as Movie;
                if (movie == null) return;

                var user = e.User;
                var favorite = e.UserData.IsFavorite;
                var key = PlayedStateTracker.KeyFor(user.InternalId, movie.InternalId);
                if (!_state.ShouldDispatch(key, favorite, DateTime.UtcNow)) return;
                if (!Eligible(user, movie)) return;

                var ids = TrackingBuilder.PickMovieId(ItemMapper.IdsOf(movie));
                if (ids == null) return;

                Send(user, movie.Name, ids, favorite);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("WeTrakr could not queue a favorite change", ex);
            }
        }

        private bool Eligible(User user, BaseItem item)
        {
            if (!user.IsGrantedAccessToFeature(Plugin.StaticId)) return false;
            if (!ConfigurationFactory.IsConnected(_users, user.InternalId)) return false;

            var options = ConfigurationFactory.LoadOptions(_users, user.InternalId);
            if (!options.syncFavorites) return false;
            return !ExclusionFilter.For(_library, user, options).IsExcluded(item);
        }

        private async void Send(User user, string name, IdSet ids, bool favorite)
        {
            try
            {
                var body = new FavoritesBody { Movies = new List<FavoriteMovie> { new FavoriteMovie { Ids = ids } } };
                if (favorite) await _api.AddFavorites(user.InternalId, body, CancellationToken.None).ConfigureAwait(false);
                else await _api.RemoveFavorites(user.InternalId, body, CancellationToken.None).ConfigureAwait(false);
                _logger.Info("WeTrakr {0} favorite for {1}: {2}", favorite ? "added" : "removed", user.Name, name);
            }
            catch (NotConnectedException)
            {
                _logger.Info("WeTrakr login of {0} is no longer valid; they need to connect again", user.Name);
            }
            catch (ApiKeyMissingException)
            {
                _logger.Debug("WeTrakr has no API key configured; not reporting a favorite change for {0}", name);
            }
            catch (QuotaExceededException ex)
            {
                _logger.Info("WeTrakr quota reached for {0}: {1}", user.Name, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("WeTrakr could not send a favorite change for " + user.Name, ex);
            }
        }
    }
}
