using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Emby.Plugin.WeTrakr.Api;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Emby.Plugin.WeTrakr.Services
{
    /// <summary>
    /// What one user can see in the library, indexed by external id so a WeTrakr entry finds its
    /// Emby item without a query per entry. Movies and series/episodes are read on first use, so
    /// a run that only needs movies never walks the episodes. Excluded libraries are left out,
    /// which is what keeps them out of both directions of a sync.
    /// </summary>
    public class LibraryIndex
    {
        private readonly ILibraryManager _library;
        private readonly User _user;
        private readonly ExclusionFilter _exclusions;
        private readonly CancellationToken _ct;

        private bool _moviesRead;
        private bool _showsRead;
        private readonly List<Movie> _movies = new List<Movie>();
        private readonly List<Episode> _episodes = new List<Episode>();
        private readonly Dictionary<string, Movie> _movieByKey = new Dictionary<string, Movie>();
        private readonly Dictionary<string, Series> _seriesByKey = new Dictionary<string, Series>();
        private readonly Dictionary<string, Episode> _episodeByCoords = new Dictionary<string, Episode>();
        private readonly Dictionary<string, Episode> _episodeByTvdb = new Dictionary<string, Episode>();

        public LibraryIndex(ILibraryManager library, User user, ExclusionFilter exclusions, CancellationToken ct)
        {
            _library = library;
            _user = user;
            _exclusions = exclusions;
            _ct = ct;
        }

        public IReadOnlyList<Movie> Movies { get { ReadMovies(); return _movies; } }
        public IReadOnlyList<Episode> Episodes { get { ReadShows(); return _episodes; } }

        public string Describe()
        {
            return "indexed " + (_moviesRead ? _movies.Count + " movies" : "no movies") + ", " + (_showsRead ? _seriesByKey.Values.Distinct().Count() + " series, " + _episodes.Count + " episodes" : "no series");
        }

        public Movie FindMovie(IdSet ids)
        {
            ReadMovies();
            return ids == null ? null : Lookup(_movieByKey, Keys(ids, false));
        }

        public Series FindSeries(IdSet ids)
        {
            ReadShows();
            return ids == null ? null : Lookup(_seriesByKey, Keys(ids, true));
        }

        /// <summary>An episode by its own TVDB id when WeTrakr sent one, otherwise by season and number inside the series.</summary>
        public Episode FindEpisode(Series series, int season, int number, IdSet episodeIds)
        {
            ReadShows();
            Episode found;
            if (!string.IsNullOrEmpty(episodeIds?.Tvdb) && _episodeByTvdb.TryGetValue(episodeIds.Tvdb, out found)) return found;
            if (series != null && _episodeByCoords.TryGetValue(Coord(series.InternalId, season, number), out found)) return found;
            return null;
        }

        private void ReadMovies()
        {
            if (_moviesRead) return;
            _moviesRead = true;
            foreach (var item in Query("Movie"))
            {
                var movie = item as Movie;
                if (movie == null || _exclusions.IsExcluded(movie)) continue;
                _movies.Add(movie);
                foreach (var key in Keys(ItemMapper.IdsOf(movie), false)) Put(_movieByKey, key, movie);
            }
        }

        private void ReadShows()
        {
            if (_showsRead) return;
            _showsRead = true;

            foreach (var item in Query("Series"))
            {
                var series = item as Series;
                if (series == null) continue;
                foreach (var key in Keys(ItemMapper.IdsOf(series), true)) Put(_seriesByKey, key, series);
            }

            foreach (var item in Query("Episode"))
            {
                var episode = item as Episode;
                if (episode == null || _exclusions.IsExcluded(episode)) continue;
                _episodes.Add(episode);

                string tvdb;
                if (episode.ProviderIds != null && episode.ProviderIds.TryGetValue("Tvdb", out tvdb) && !string.IsNullOrEmpty(tvdb)) Put(_episodeByTvdb, tvdb, episode);

                if (episode.ParentIndexNumber == null || episode.IndexNumber == null) continue;
                var first = episode.IndexNumber.Value;
                var last = Math.Max(first, episode.IndexNumberEnd ?? first);   // one file can hold several episodes
                for (var number = first; number <= last; number++) Put(_episodeByCoords, Coord(episode.SeriesId, episode.ParentIndexNumber.Value, number), episode);
            }
        }

        // asked from the user's root folder, so library access and parental rules already apply
        private BaseItem[] Query(string type)
        {
            _ct.ThrowIfCancellationRequested();
            return _library.GetUserRootFolder().GetItemList(new InternalItemsQuery(_user)
            {
                IncludeItemTypes = new[] { type },
                Recursive = true,
                IsVirtualItem = false,
                EnableTotalRecordCount = false,
                DtoOptions = new DtoOptions { EnableImages = false, EnableUserData = false }
            });
        }

        private static IEnumerable<string> Keys(IdSet ids, bool withTvdb)
        {
            if (withTvdb && !string.IsNullOrEmpty(ids.Tvdb)) yield return "tvdb:" + ids.Tvdb;
            if (!string.IsNullOrEmpty(ids.Imdb)) yield return "imdb:" + ids.Imdb.ToLowerInvariant();
            if (!string.IsNullOrEmpty(ids.Tmdb)) yield return "tmdb:" + ids.Tmdb;
            // a movie's TVDB id is rarely present; when it is, it still identifies the movie
            if (!withTvdb && !string.IsNullOrEmpty(ids.Tvdb)) yield return "tvdb:" + ids.Tvdb;
        }

        private static T Lookup<T>(Dictionary<string, T> index, IEnumerable<string> keys) where T : class
        {
            foreach (var key in keys)
            {
                T item;
                if (index.TryGetValue(key, out item)) return item;
            }
            return null;
        }

        // first item wins, so duplicates in a library stay deterministic
        private static void Put<T>(Dictionary<string, T> index, string key, T item)
        {
            if (!index.ContainsKey(key)) index[key] = item;
        }

        private static string Coord(long seriesId, int season, int number)
        {
            return seriesId + ":" + season + ":" + number;
        }
    }
}
