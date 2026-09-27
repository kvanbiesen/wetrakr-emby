using System;
using Emby.Plugin.WeTrakr.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace Emby.Plugin.WeTrakr.Services
{
    /// <summary>Turns Emby items into the ids and request bodies WeTrakr wants.</summary>
    public static class ItemMapper
    {
        public static bool IsTrackable(BaseItem item)
        {
            return item is Movie || item is Episode;
        }

        public static IdSet IdsOf(BaseItem item)
        {
            return IdsOf(item?.ProviderIds);
        }

        public static IdSet IdsOf(ProviderIdDictionary providers)
        {
            var ids = new IdSet();
            if (providers == null) return ids;
            string value;
            if (providers.TryGetValue("Tmdb", out value) && !string.IsNullOrEmpty(value)) ids.Tmdb = value;
            if (providers.TryGetValue("Imdb", out value) && !string.IsNullOrEmpty(value)) ids.Imdb = value;
            if (providers.TryGetValue("Tvdb", out value) && !string.IsNullOrEmpty(value)) ids.Tvdb = value;
            return ids;
        }

        /// <summary>A watched movie or episode as plain data; null for anything else, or an episode that has no place in a show.</summary>
        public static WatchedItem ToWatched(BaseItem item, DateTime watchedUtc)
        {
            var movie = item as Movie;
            if (movie != null)
            {
                return new WatchedItem { IsEpisode = false, Title = movie.Name, Ids = IdsOf(movie), WatchedAtUtc = watchedUtc };
            }

            var episode = item as Episode;
            if (episode == null || episode.ParentIndexNumber == null || episode.IndexNumber == null) return null;
            var series = episode.Series;
            return new WatchedItem
            {
                IsEpisode = true,
                Title = episode.Name,
                EpisodeIds = IdsOf(episode),
                ShowIds = IdsOf(series),
                Season = episode.ParentIndexNumber.Value,
                Number = episode.IndexNumber.Value,
                WatchedAtUtc = watchedUtc
            };
        }

        /// <summary>
        /// The scrobble body for playback at a position, or null when WeTrakr could not place the
        /// item (an episode with no series or numbers). The app_version names Emby and its version.
        /// </summary>
        public static ScrobbleBody ToScrobble(BaseItem item, double progress)
        {
            var body = new ScrobbleBody { Progress = Math.Round(Math.Max(0, Math.Min(100, progress)), 1), AppVersion = ClientInfo.AppVersion };

            var movie = item as Movie;
            if (movie != null)
            {
                body.Movie = new ScrobbleMedia { Title = movie.Name, Year = movie.ProductionYear, Ids = IdsOf(movie) };
                return body;
            }

            var episode = item as Episode;
            if (episode == null || episode.ParentIndexNumber == null || episode.IndexNumber == null) return null;
            var series = episode.Series;
            body.Show = new ScrobbleMedia { Title = series?.Name ?? episode.SeriesName, Year = series?.ProductionYear, Ids = IdsOf(series) };
            body.Episode = new ScrobbleEpisode { Season = episode.ParentIndexNumber.Value, Number = episode.IndexNumber.Value, Title = episode.Name };
            return body;
        }

        public static double? Progress(long? positionTicks, long? runTimeTicks)
        {
            if (positionTicks == null || runTimeTicks == null || runTimeTicks <= 0) return null;
            return Math.Max(0, Math.Min(100, positionTicks.Value * 100.0 / runTimeTicks.Value));
        }
    }
}
