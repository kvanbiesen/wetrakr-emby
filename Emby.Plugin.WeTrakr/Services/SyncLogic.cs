using System;
using System.Collections.Generic;
using System.Globalization;
using Emby.Plugin.WeTrakr.Api;
using Emby.Plugin.WeTrakr.Configuration;

namespace Emby.Plugin.WeTrakr.Services
{
    /// <summary>
    /// The plain rules of a sync run, apart from Emby and the network so they can be tested.
    /// </summary>
    public static class SyncLogic
    {
        public const string DateFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

        // last_activities is written slightly after the rows it describes and from_date is a strict
        // "after", so a pull looks back a little rather than miss a change made during the last run
        public static readonly TimeSpan DateFromSlack = TimeSpan.FromSeconds(60);

        public static DateTime? ParseDate(string value)
        {
            DateTime date;
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out date)) return null;
            // WeTrakr uses a very old date for "watched, date unknown"
            return date.Year < 2000 ? (DateTime?)null : date;
        }

        public static string Stamp(DateTime utc)
        {
            return DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString(DateFormat, CultureInfo.InvariantCulture);
        }

        /// <summary>The three watched-history timestamps of last_activities that decide whether anything moved.</summary>
        public class Cursors
        {
            public string Movies { get; set; }
            public string Episodes { get; set; }
            public string Shows { get; set; }

            public static Cursors From(Activities activities)
            {
                return new Cursors
                {
                    Movies = activities?.Movies?.LastTrackingWatchedAt,
                    Episodes = activities?.Episodes?.LastTrackingWatchedAt,
                    Shows = activities?.Shows?.LastTrackingWatchedAt
                };
            }
        }

        /// <summary>
        /// The most plays one sync will send from Emby. Anything live is already on WeTrakr by the time a sync
        /// runs (scrobbles and mark-as-played go out as they happen), so a sync only catches up on what was
        /// missed, which is a handful. A large number appearing at once looks like a glitch in Emby, such as a
        /// library rebuilt with everything marked played, and is left alone instead of being written to WeTrakr.
        /// </summary>
        public const int MaxPushItems = 250;

        public class Plan
        {
            /// <summary>Read the whole watched history from WeTrakr (first import or a reset).</summary>
            public bool Full { get; set; }

            /// <summary>Read from WeTrakr at all: a full read, or only what changed since the last sync.</summary>
            public bool Read { get; set; }
        }

        /// <summary>
        /// Decides what a run reads. Only importing needs the whole history; a user who only sends starts from a
        /// recorded point and never reads the whole list, which matters on a large account.
        /// </summary>
        public static Plan PlanRun(SyncState state, UserOptions options, bool reset, Cursors now)
        {
            var full = options.syncPull && (reset || string.IsNullOrEmpty(state.fullFetchAt));
            var neverSynced = string.IsNullOrEmpty(state.syncedAt);
            return new Plan { Full = full, Read = full || (!neverSynced && Moved(state, now)) };
        }

        public enum PushDecision { Baseline, Nothing, Send, OverLimit }

        /// <summary>
        /// The first run with sending on only records where it starts, so Emby history from before is never
        /// sent in bulk. After that, what changed is sent unless it is more than <see cref="MaxPushItems"/>.
        /// </summary>
        public static PushDecision DecidePush(bool hasBaseline, int candidates, int max = MaxPushItems)
        {
            if (!hasBaseline) return PushDecision.Baseline;
            if (candidates <= 0) return PushDecision.Nothing;
            return candidates > max ? PushDecision.OverLimit : PushDecision.Send;
        }

        /// <summary>True when any watched timestamp is newer than the one stored last run (or none is stored yet).</summary>
        public static bool Moved(SyncState state, Cursors now)
        {
            return Newer(now.Movies, state.cursorMovies) || Newer(now.Episodes, state.cursorEpisodes) || Newer(now.Shows, state.cursorShows);
        }

        private static bool Newer(string latest, string stored)
        {
            if (string.IsNullOrEmpty(latest)) return false;
            if (string.IsNullOrEmpty(stored)) return true;
            var a = ParseDate(latest);
            var b = ParseDate(stored);
            if (a.HasValue && b.HasValue) return a.Value > b.Value;
            return string.CompareOrdinal(latest, stored) > 0;
        }

        public static void Remember(SyncState state, Cursors now)
        {
            if (!string.IsNullOrEmpty(now.Movies)) state.cursorMovies = now.Movies;
            if (!string.IsNullOrEmpty(now.Episodes)) state.cursorEpisodes = now.Episodes;
            if (!string.IsNullOrEmpty(now.Shows)) state.cursorShows = now.Shows;
        }

        /// <summary>The from_date of an incremental read: where the last good run started reading, minus the slack.</summary>
        public static string FromDate(SyncState state)
        {
            var since = ParseDate(state.syncedAt) ?? ParseDate(state.fullFetchAt);
            return since.HasValue ? Stamp(since.Value - DateFromSlack) : null;
        }

        /// <summary>A scheduled run for a user is due once their own interval has passed since the last one (5 minutes of tick drift allowed).</summary>
        public static bool Due(SyncState state, int hours, DateTime nowUtc)
        {
            var last = ParseDate(state.lastAutoRunAt);
            if (!last.HasValue) return true;
            var interval = TimeSpan.FromHours(Math.Max(1, hours));
            return nowUtc - last.Value >= interval - TimeSpan.FromMinutes(5);
        }
    }

    /// <summary>
    /// What WeTrakr already lists as watched, as keys, so sending Emby's history never adds a
    /// second play of something WeTrakr has. A movie is known by any of its ids; an episode by
    /// any id of its show plus season and number, or by its own TVDB id.
    /// </summary>
    public class RemoteKeys
    {
        private readonly HashSet<string> _keys = new HashSet<string>();

        public int Count => _keys.Count;

        public void AddMovie(IdSet ids)
        {
            foreach (var key in MovieKeys(ids)) _keys.Add(key);
        }

        public void AddEpisode(IdSet showIds, int season, int number, IdSet episodeIds)
        {
            foreach (var key in EpisodeKeys(showIds, season, number, episodeIds)) _keys.Add(key);
        }

        public bool HasMovie(IdSet ids)
        {
            foreach (var key in MovieKeys(ids))
            {
                if (_keys.Contains(key)) return true;
            }
            return false;
        }

        public bool HasEpisode(IdSet showIds, int season, int number, IdSet episodeIds)
        {
            foreach (var key in EpisodeKeys(showIds, season, number, episodeIds))
            {
                if (_keys.Contains(key)) return true;
            }
            return false;
        }

        private static IEnumerable<string> MovieKeys(IdSet ids)
        {
            if (ids == null) yield break;
            if (!string.IsNullOrEmpty(ids.Tmdb)) yield return "m:tmdb:" + ids.Tmdb;
            if (!string.IsNullOrEmpty(ids.Imdb)) yield return "m:imdb:" + ids.Imdb.ToLowerInvariant();
            if (!string.IsNullOrEmpty(ids.Tvdb)) yield return "m:tvdb:" + ids.Tvdb;
        }

        private static IEnumerable<string> EpisodeKeys(IdSet showIds, int season, int number, IdSet episodeIds)
        {
            if (!string.IsNullOrEmpty(episodeIds?.Tvdb)) yield return "e:tvdb:" + episodeIds.Tvdb;
            if (showIds == null) yield break;
            var tail = "|" + season.ToString(CultureInfo.InvariantCulture) + "|" + number.ToString(CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(showIds.Tmdb)) yield return "s:tmdb:" + showIds.Tmdb + tail;
            if (!string.IsNullOrEmpty(showIds.Tvdb)) yield return "s:tvdb:" + showIds.Tvdb + tail;
            if (!string.IsNullOrEmpty(showIds.Imdb)) yield return "s:imdb:" + showIds.Imdb.ToLowerInvariant() + tail;
        }
    }
}
