using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Emby.Plugin.WeTrakr.Api;

namespace Emby.Plugin.WeTrakr.Services
{
    /// <summary>One watched movie or episode, stripped of Emby types so the request building stays plain and testable.</summary>
    public class WatchedItem
    {
        public bool IsEpisode { get; set; }
        public string Title { get; set; }

        /// <summary>Movie ids (unused for episodes: WeTrakr addresses an episode by show + season + number).</summary>
        public IdSet Ids { get; set; }

        /// <summary>The episode's own ids, used to remove a single play; adding goes by show, season and number.</summary>
        public IdSet EpisodeIds { get; set; }

        /// <summary>Series ids of an episode.</summary>
        public IdSet ShowIds { get; set; }

        public int Season { get; set; }
        public int Number { get; set; }
        public DateTime WatchedAtUtc { get; set; }
    }

    /// <summary>
    /// Builds POST /sync/tracking bodies. WeTrakr wants one external id per item, and takes
    /// a show's episodes nested by season and number: the documented shape for sending a history.
    /// </summary>
    public static class TrackingBuilder
    {
        public const string Watched = "watched";

        // A show that only gets some episodes marked stays "watching". WeTrakr moves it on to
        // watched or waiting itself once the last aired episode is in, so this never forces a
        // finished show back: it is the status the API documents for a partial history.
        public const string PartialShowStatus = "watching";

        public static string FormatDate(DateTime utc)
        {
            return DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        }

        /// <summary>The single id WeTrakr resolves a movie by: TMDB first (its native catalog), then IMDb, then TVDB.</summary>
        public static IdSet PickMovieId(IdSet ids)
        {
            if (ids == null) return null;
            if (!string.IsNullOrEmpty(ids.Tmdb)) return new IdSet { Tmdb = ids.Tmdb };
            if (!string.IsNullOrEmpty(ids.Imdb)) return new IdSet { Imdb = ids.Imdb };
            if (!string.IsNullOrEmpty(ids.Tvdb)) return new IdSet { Tvdb = ids.Tvdb };
            return null;
        }

        /// <summary>The single id for a show: TMDB, then TVDB (Emby's usual series id), then IMDb.</summary>
        public static IdSet PickShowId(IdSet ids)
        {
            if (ids == null) return null;
            if (!string.IsNullOrEmpty(ids.Tmdb)) return new IdSet { Tmdb = ids.Tmdb };
            if (!string.IsNullOrEmpty(ids.Tvdb)) return new IdSet { Tvdb = ids.Tvdb };
            if (!string.IsNullOrEmpty(ids.Imdb)) return new IdSet { Imdb = ids.Imdb };
            return null;
        }

        private static string Key(IdSet ids)
        {
            return ids.Tmdb != null ? "tmdb:" + ids.Tmdb : ids.Tvdb != null ? "tvdb:" + ids.Tvdb : "imdb:" + ids.Imdb;
        }

        /// <summary>
        /// Items WeTrakr cannot address (no usable id, or an episode with no season/number) are
        /// counted in skipped rather than sent. The same item twice keeps its latest date.
        /// </summary>
        public static TrackingBody Build(IEnumerable<WatchedItem> items, out int skipped)
        {
            skipped = 0;
            var movies = new Dictionary<string, TrackingMovie>();
            var shows = new Dictionary<string, TrackingShow>();
            var showOrder = new List<string>();
            var episodeDates = new Dictionary<string, TrackingEpisode>();

            foreach (var item in items)
            {
                if (!item.IsEpisode)
                {
                    var id = PickMovieId(item.Ids);
                    if (id == null) { skipped++; continue; }
                    var key = Key(id);
                    TrackingMovie existing;
                    if (!movies.TryGetValue(key, out existing) || string.CompareOrdinal(TrackingBuilder.FormatDate(item.WatchedAtUtc), existing.TrackedAt) > 0)
                    {
                        movies[key] = new TrackingMovie { Ids = id, Status = Watched, TrackedAt = FormatDate(item.WatchedAtUtc) };
                    }
                    continue;
                }

                var showId = PickShowId(item.ShowIds);
                if (showId == null || item.Number <= 0 || item.Season < 0) { skipped++; continue; }

                var showKey = Key(showId);
                TrackingShow show;
                if (!shows.TryGetValue(showKey, out show))
                {
                    show = new TrackingShow { Ids = showId, Status = PartialShowStatus, Seasons = new List<TrackingSeason>() };
                    shows[showKey] = show;
                    showOrder.Add(showKey);
                }

                var episodeKey = showKey + "|" + item.Season + "|" + item.Number;
                TrackingEpisode episode;
                if (episodeDates.TryGetValue(episodeKey, out episode))
                {
                    if (string.CompareOrdinal(FormatDate(item.WatchedAtUtc), episode.TrackedAt) > 0) episode.TrackedAt = FormatDate(item.WatchedAtUtc);
                    continue;
                }

                var season = show.Seasons.FirstOrDefault(s => s.Number == item.Season);
                if (season == null)
                {
                    season = new TrackingSeason { Number = item.Season, Episodes = new List<TrackingEpisode>() };
                    show.Seasons.Add(season);
                }
                episode = new TrackingEpisode { Number = item.Number, Status = Watched, TrackedAt = FormatDate(item.WatchedAtUtc) };
                season.Episodes.Add(episode);
                episodeDates[episodeKey] = episode;
            }

            return new TrackingBody
            {
                Movies = movies.Count > 0 ? movies.Values.ToList() : null,
                Shows = showOrder.Count > 0 ? showOrder.Select(k => shows[k]).ToList() : null
            };
        }

        /// <summary>The single id for an episode addressed on its own: TMDB, then TVDB (Emby's usual episode id), then IMDb.</summary>
        public static IdSet PickEpisodeId(IdSet ids)
        {
            if (ids == null) return null;
            if (!string.IsNullOrEmpty(ids.Tmdb)) return new IdSet { Tmdb = ids.Tmdb };
            if (!string.IsNullOrEmpty(ids.Tvdb)) return new IdSet { Tvdb = ids.Tvdb };
            if (!string.IsNullOrEmpty(ids.Imdb)) return new IdSet { Imdb = ids.Imdb };
            return null;
        }

        /// <summary>
        /// Body for taking single plays off the history (POST /sync/tracking/remove). No tracked_at is sent, which
        /// makes WeTrakr remove only the latest play of each item named - never the whole history of a season or
        /// show. A movie is addressed by its own external id, same as adding one. WeTrakr's top-level "episodes"
        /// field only takes its own internal id (which Emby has no way to know), so an episode is nested under its
        /// show and season by number instead, exactly like adding one - just scoped to the one episode being
        /// removed rather than a whole season or show. An item with no usable id is counted in skipped rather than
        /// guessed at.
        /// </summary>
        public static TrackingBody BuildRemoval(IEnumerable<WatchedItem> items, out int skipped)
        {
            skipped = 0;
            var movies = new List<TrackingMovie>();
            var shows = new Dictionary<string, TrackingShow>();
            var showOrder = new List<string>();

            foreach (var item in items)
            {
                if (!item.IsEpisode)
                {
                    var movieId = PickMovieId(item.Ids);
                    if (movieId == null) { skipped++; continue; }
                    movies.Add(new TrackingMovie { Ids = movieId, Status = Watched });
                    continue;
                }

                var showId = PickShowId(item.ShowIds);
                if (showId == null || item.Number <= 0 || item.Season < 0) { skipped++; continue; }

                var showKey = Key(showId);
                TrackingShow show;
                if (!shows.TryGetValue(showKey, out show))
                {
                    show = new TrackingShow { Ids = showId, Seasons = new List<TrackingSeason>() };
                    shows[showKey] = show;
                    showOrder.Add(showKey);
                }

                var season = show.Seasons.FirstOrDefault(s => s.Number == item.Season);
                if (season == null)
                {
                    season = new TrackingSeason { Number = item.Season, Episodes = new List<TrackingEpisode>() };
                    show.Seasons.Add(season);
                }
                if (!season.Episodes.Any(ep => ep.Number == item.Number))
                    season.Episodes.Add(new TrackingEpisode { Number = item.Number, Status = Watched });
            }

            return new TrackingBody
            {
                Movies = movies.Count > 0 ? movies : null,
                Shows = showOrder.Count > 0 ? showOrder.Select(k => shows[k]).ToList() : null
            };
        }

        /// <summary>Number of plays a body carries (movies plus episodes; the limit WeTrakr applies per call).</summary>
        public static int Count(TrackingBody body)
        {
            var count = body.Movies?.Count ?? 0;
            if (body.Shows != null)
            {
                foreach (var show in body.Shows)
                {
                    foreach (var season in show.Seasons ?? new List<TrackingSeason>()) count += season.Episodes?.Count ?? 0;
                }
            }
            return count;
        }
    }

    /// <summary>
    /// Cuts a tracking body into calls WeTrakr accepts: at most 5,000 items and 1 MB each.
    /// The byte size is estimated from the entries, kept well under the limit, rather than
    /// measured by serialising each call twice.
    /// </summary>
    public static class TrackingBatcher
    {
        public const int MaxItems = 5000;
        public const int MaxBytes = 800000;

        private const int MovieBytes = 110;
        private const int EpisodeBytes = 70;
        private const int ShowBytes = 90;
        private const int SeasonBytes = 40;

        public static IEnumerable<TrackingBody> Split(TrackingBody body, int maxItems = MaxItems, int maxBytes = MaxBytes)
        {
            var batch = new BatchBuilder(maxItems, maxBytes);

            foreach (var movie in body.Movies ?? new List<TrackingMovie>())
            {
                if (batch.Full(1, MovieBytes)) yield return batch.Take();
                batch.AddMovie(movie);
            }

            foreach (var show in body.Shows ?? new List<TrackingShow>())
            {
                foreach (var season in show.Seasons ?? new List<TrackingSeason>())
                {
                    foreach (var episode in season.Episodes ?? new List<TrackingEpisode>())
                    {
                        if (batch.Full(1, batch.EpisodeCost(show, season))) yield return batch.Take();
                        batch.AddEpisode(show, season, episode);
                    }
                }
            }

            if (!batch.IsEmpty) yield return batch.Take();
        }

        private class BatchBuilder
        {
            private readonly int _maxItems;
            private readonly int _maxBytes;
            private List<TrackingMovie> _movies = new List<TrackingMovie>();
            private List<TrackingShow> _shows = new List<TrackingShow>();
            private readonly Dictionary<TrackingShow, TrackingShow> _showCopies = new Dictionary<TrackingShow, TrackingShow>();
            private readonly Dictionary<TrackingSeason, TrackingSeason> _seasonCopies = new Dictionary<TrackingSeason, TrackingSeason>();
            private int _items;
            private int _bytes;

            public BatchBuilder(int maxItems, int maxBytes)
            {
                _maxItems = maxItems;
                _maxBytes = maxBytes;
            }

            public bool IsEmpty => _items == 0;

            // an empty batch never counts as full, so a single oversized entry still goes out
            public bool Full(int items, int bytes)
            {
                return _items > 0 && (_items + items > _maxItems || _bytes + bytes > _maxBytes);
            }

            public int EpisodeCost(TrackingShow show, TrackingSeason season)
            {
                var cost = EpisodeBytes;
                TrackingShow copy;
                if (!_showCopies.TryGetValue(show, out copy)) cost += ShowBytes + SeasonBytes;
                else if (!_seasonCopies.ContainsKey(season)) cost += SeasonBytes;
                return cost;
            }

            public void AddMovie(TrackingMovie movie)
            {
                _movies.Add(movie);
                _items++;
                _bytes += MovieBytes;
            }

            public void AddEpisode(TrackingShow show, TrackingSeason season, TrackingEpisode episode)
            {
                TrackingShow showCopy;
                if (!_showCopies.TryGetValue(show, out showCopy))
                {
                    showCopy = new TrackingShow { Ids = show.Ids, Status = show.Status, Seasons = new List<TrackingSeason>() };
                    _showCopies[show] = showCopy;
                    _shows.Add(showCopy);
                    _bytes += ShowBytes;
                }

                TrackingSeason seasonCopy;
                if (!_seasonCopies.TryGetValue(season, out seasonCopy))
                {
                    seasonCopy = new TrackingSeason { Number = season.Number, Episodes = new List<TrackingEpisode>() };
                    _seasonCopies[season] = seasonCopy;
                    showCopy.Seasons.Add(seasonCopy);
                    _bytes += SeasonBytes;
                }

                seasonCopy.Episodes.Add(episode);
                _items++;
                _bytes += EpisodeBytes;
            }

            public TrackingBody Take()
            {
                var body = new TrackingBody
                {
                    Movies = _movies.Count > 0 ? _movies : null,
                    Shows = _shows.Count > 0 ? _shows : null
                };
                _movies = new List<TrackingMovie>();
                _shows = new List<TrackingShow>();
                _showCopies.Clear();
                _seasonCopies.Clear();
                _items = 0;
                _bytes = 0;
                return body;
            }
        }
    }
}
