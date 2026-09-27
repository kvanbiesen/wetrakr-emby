using System;
using Emby.Plugin.WeTrakr.Api;
using Emby.Plugin.WeTrakr.Configuration;
using Emby.Plugin.WeTrakr.Services;
using Xunit;

namespace Emby.Plugin.WeTrakr.Tests
{
    public class SyncLogicTests
    {
        private static readonly DateTime Now = new DateTime(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Nothing_moved_when_the_watched_timestamps_are_unchanged()
        {
            var state = new SyncState { cursorMovies = "2026-09-22T01:18:05.815Z", cursorEpisodes = "2026-09-17T07:53:26.540Z", cursorShows = "2026-05-11T20:30:00.000Z" };
            var now = new SyncLogic.Cursors { Movies = "2026-09-22T01:18:05.815Z", Episodes = "2026-09-17T07:53:26.540Z", Shows = "2026-05-11T20:30:00.000Z" };
            Assert.False(SyncLogic.Moved(state, now));
        }

        [Fact]
        public void Any_newer_watched_timestamp_means_something_moved()
        {
            var state = new SyncState { cursorMovies = "2026-09-22T01:18:05.815Z", cursorEpisodes = "2026-09-17T07:53:26.540Z" };
            Assert.True(SyncLogic.Moved(state, new SyncLogic.Cursors { Movies = "2026-09-22T01:18:05.815Z", Episodes = "2026-09-18T00:00:00.000Z" }));
        }

        [Fact]
        public void A_timestamp_never_stored_counts_as_moved_but_an_absent_one_does_not()
        {
            Assert.True(SyncLogic.Moved(new SyncState(), new SyncLogic.Cursors { Movies = "2026-09-22T01:18:05.815Z" }));
            Assert.False(SyncLogic.Moved(new SyncState(), new SyncLogic.Cursors()));
        }

        [Fact]
        public void An_absent_field_is_not_an_error_and_never_moves_backwards()
        {
            var state = new SyncState { cursorMovies = "2026-09-22T01:18:05.815Z" };
            SyncLogic.Remember(state, new SyncLogic.Cursors { Episodes = "2026-09-20T00:00:00.000Z" });
            Assert.Equal("2026-09-22T01:18:05.815Z", state.cursorMovies);
            Assert.Equal("2026-09-20T00:00:00.000Z", state.cursorEpisodes);
        }

        [Fact]
        public void Cursors_are_read_from_last_activities()
        {
            var activities = new Activities
            {
                Movies = new ActivityBlock { LastTrackingWatchedAt = "m" },
                Episodes = new ActivityBlock { LastTrackingWatchedAt = "e" },
                Shows = new ActivityBlock { LastTrackingWatchedAt = "s" }
            };
            var cursors = SyncLogic.Cursors.From(activities);
            Assert.Equal("m", cursors.Movies);
            Assert.Equal("e", cursors.Episodes);
            Assert.Equal("s", cursors.Shows);
            Assert.Null(SyncLogic.Cursors.From(null).Movies);
        }

        [Fact]
        public void The_incremental_read_looks_back_a_minute_from_the_last_run()
        {
            var state = new SyncState { syncedAt = "2026-09-26T10:00:00.000Z" };
            Assert.Equal("2026-09-26T09:59:00.000Z", SyncLogic.FromDate(state));
        }

        [Fact]
        public void Without_a_last_run_there_is_no_from_date()
        {
            Assert.Null(SyncLogic.FromDate(new SyncState()));
        }

        [Fact]
        public void The_unknown_date_sentinel_is_ignored()
        {
            Assert.Null(SyncLogic.ParseDate("1970-01-01T00:00:00.000Z"));
            Assert.Null(SyncLogic.ParseDate(""));
            Assert.Equal(new DateTime(2026, 9, 15, 21, 9, 53, 772, DateTimeKind.Utc), SyncLogic.ParseDate("2026-09-15T21:09:53.772Z"));
        }

        [Theory]
        [InlineData(null, true)]
        [InlineData("2026-09-25T12:00:00.000Z", true)]    // a full day ago: due for a daily user
        [InlineData("2026-09-26T00:00:00.000Z", false)]   // 12 hours ago
        [InlineData("2026-09-25T12:04:00.000Z", true)]    // 5 minutes of tick drift are forgiven
        public void A_daily_user_is_due_once_a_day_has_passed(string lastAuto, bool due)
        {
            var state = new SyncState { lastAutoRunAt = lastAuto ?? "" };
            Assert.Equal(due, SyncLogic.Due(state, 24, Now));
        }
    }

    public class RemoteKeysTests
    {
        [Fact]
        public void A_movie_is_known_by_any_one_of_its_ids()
        {
            var remote = new RemoteKeys();
            remote.AddMovie(new IdSet { Tmdb = "155", Imdb = "TT0468569" });
            Assert.True(remote.HasMovie(new IdSet { Imdb = "tt0468569" }));
            Assert.True(remote.HasMovie(new IdSet { Tmdb = "155" }));
            Assert.False(remote.HasMovie(new IdSet { Tmdb = "156" }));
            Assert.False(remote.HasMovie(new IdSet()));
        }

        [Fact]
        public void An_episode_is_known_by_show_season_and_number_under_any_show_id()
        {
            var remote = new RemoteKeys();
            remote.AddEpisode(new IdSet { Tmdb = "1399", Tvdb = "121361" }, 2, 5, null);
            Assert.True(remote.HasEpisode(new IdSet { Tvdb = "121361" }, 2, 5, null));   // Emby usually knows the show by TVDB
            Assert.True(remote.HasEpisode(new IdSet { Tmdb = "1399" }, 2, 5, null));
            Assert.False(remote.HasEpisode(new IdSet { Tvdb = "121361" }, 2, 6, null));
            Assert.False(remote.HasEpisode(new IdSet { Tvdb = "999" }, 2, 5, null));
        }

        [Fact]
        public void An_episode_is_also_known_by_its_own_tvdb_id()
        {
            var remote = new RemoteKeys();
            remote.AddEpisode(new IdSet { Tmdb = "1" }, 1, 1, new IdSet { Tvdb = "349232" });
            Assert.True(remote.HasEpisode(new IdSet { Tvdb = "other" }, 9, 9, new IdSet { Tvdb = "349232" }));
        }

        [Fact]
        public void A_movie_key_never_matches_a_show_key()
        {
            var remote = new RemoteKeys();
            remote.AddMovie(new IdSet { Tmdb = "1399" });
            Assert.False(remote.HasEpisode(new IdSet { Tmdb = "1399" }, 1, 1, null));
        }
    }

    public class ExclusionAndTrackerTests
    {
        [Theory]
        [InlineData("/media/tv/Show/S01E01.mkv", "/media/tv", true)]
        [InlineData("/media/tv/Show/S01E01.mkv", "/media/tv/", true)]
        [InlineData("/media/tv2/Show/S01E01.mkv", "/media/tv", false)]   // a sibling folder that shares a prefix
        [InlineData("D:\\Media\\Kids\\Movie.mkv", "D:\\Media\\Kids", true)]
        [InlineData("d:\\media\\kids\\Movie.mkv", "D:\\Media\\Kids", true)]
        [InlineData("D:\\Media\\KidsExtra\\Movie.mkv", "D:\\Media\\Kids", false)]
        [InlineData("/media/tv", "/media/tv", true)]
        [InlineData("/other/x.mkv", "/media/tv", false)]
        public void A_path_is_inside_a_library_folder_only_at_a_folder_boundary(string path, string root, bool expected)
        {
            Assert.Equal(expected, ExclusionFilter.IsUnder(path, root));
        }

        [Fact]
        public void Any_of_several_roots_excludes()
        {
            Assert.True(ExclusionFilter.IsUnderAny("/b/x.mkv", new[] { "/a", "/b" }));
            Assert.False(ExclusionFilter.IsUnderAny("/c/x.mkv", new[] { "/a", "/b" }));
        }

        [Fact]
        public void A_played_state_only_passes_when_it_flips()
        {
            var tracker = new PlayedStateTracker();
            var key = PlayedStateTracker.KeyFor(1, 42);
            var now = DateTime.UtcNow;

            Assert.True(tracker.ShouldDispatch(key, true, now));     // first sighting always passes
            Assert.False(tracker.ShouldDispatch(key, true, now));    // a redundant re-mark
            Assert.False(tracker.ShouldDispatch(key, true, now));
            Assert.True(tracker.ShouldDispatch(key, false, now));    // unmarked
            Assert.True(tracker.ShouldDispatch(key, true, now));     // marked again
        }

        [Fact]
        public void The_same_item_for_two_users_is_tracked_separately()
        {
            var tracker = new PlayedStateTracker();
            var now = DateTime.UtcNow;
            Assert.True(tracker.ShouldDispatch(PlayedStateTracker.KeyFor(1, 42), true, now));
            Assert.True(tracker.ShouldDispatch(PlayedStateTracker.KeyFor(2, 42), true, now));
        }
    }
}
