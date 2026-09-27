using System;
using System.Linq;
using System.Text.Json;
using Emby.Plugin.WeTrakr.Api;
using Emby.Plugin.WeTrakr.Configuration;
using Emby.Plugin.WeTrakr.Services;
using Xunit;

namespace Emby.Plugin.WeTrakr.Tests
{
    public class RemovalTests
    {
        private static string Json(TrackingBody body) { return JsonSerializer.Serialize(body, WeTrakrApi.JsonOptions); }

        [Fact]
        public void A_single_episode_is_removed_by_show_season_and_number_with_no_date_or_show_status()
        {
            int skipped;
            var body = TrackingBuilder.BuildRemoval(new[]
            {
                new WatchedItem { IsEpisode = true, EpisodeIds = new IdSet { Tvdb = "349232" }, ShowIds = new IdSet { Tmdb = "1399" }, Season = 1, Number = 1 }
            }, out skipped);

            Assert.Equal(0, skipped);
            // WeTrakr's top-level "episodes" field only takes its own internal id, which Emby never has, so the
            // episode is nested under its show and season instead, exactly like adding one; no tracked_at and no
            // show status, so it can only name that one play (the latest) and nothing wider
            Assert.Equal("{\"shows\":[{\"ids\":{\"tmdb\":1399},\"seasons\":[{\"number\":1,\"episodes\":[{\"number\":1,\"status\":\"watched\"}]}]}]}", Json(body));
        }

        [Fact]
        public void A_movie_is_removed_by_id_without_a_date()
        {
            int skipped;
            var body = TrackingBuilder.BuildRemoval(new[] { new WatchedItem { IsEpisode = false, Ids = new IdSet { Tmdb = "155" }, WatchedAtUtc = DateTime.UtcNow } }, out skipped);
            Assert.Equal("{\"movies\":[{\"ids\":{\"tmdb\":155},\"status\":\"watched\"}]}", Json(body));
        }

        [Fact]
        public void An_episode_whose_show_has_no_external_id_is_left_alone_not_guessed_at()
        {
            int skipped;
            var body = TrackingBuilder.BuildRemoval(new[]
            {
                new WatchedItem { IsEpisode = true, EpisodeIds = new IdSet { Tvdb = "349232" }, ShowIds = new IdSet(), Season = 1, Number = 1 }
            }, out skipped);

            Assert.Equal(1, skipped);
            Assert.Equal(0, TrackingBuilder.Count(body));
            Assert.Equal("{}", Json(body));
        }

        [Fact]
        public void Episode_id_priority_is_tmdb_then_tvdb_then_imdb()
        {
            Assert.Equal("1", TrackingBuilder.PickEpisodeId(new IdSet { Tmdb = "1", Tvdb = "2", Imdb = "tt3" }).Tmdb);
            Assert.Equal("2", TrackingBuilder.PickEpisodeId(new IdSet { Tvdb = "2", Imdb = "tt3" }).Tvdb);
            Assert.Equal("tt3", TrackingBuilder.PickEpisodeId(new IdSet { Imdb = "tt3" }).Imdb);
            Assert.Null(TrackingBuilder.PickEpisodeId(new IdSet()));
        }

        [Fact]
        public void Removing_several_episodes_of_the_same_show_and_season_nests_them_under_one_entry()
        {
            int skipped;
            var body = TrackingBuilder.BuildRemoval(new[]
            {
                new WatchedItem { IsEpisode = true, ShowIds = new IdSet { Tmdb = "1399" }, Season = 1, Number = 1 },
                new WatchedItem { IsEpisode = true, ShowIds = new IdSet { Tmdb = "1399" }, Season = 1, Number = 2 }
            }, out skipped);

            Assert.Equal(0, skipped);
            Assert.Equal(2, TrackingBuilder.Count(body));
            Assert.Single(body.Shows);
            Assert.Single(body.Shows[0].Seasons);
            Assert.Equal(2, body.Shows[0].Seasons[0].Episodes.Count);
        }
    }

    public class PlanTests
    {
        private static readonly SyncLogic.Cursors Same = new SyncLogic.Cursors { Movies = "2026-09-22T01:18:05.815Z", Episodes = "2026-09-17T07:53:26.540Z" };

        private static SyncState Synced()
        {
            return new SyncState { fullFetchAt = "2026-09-20T00:00:00.000Z", syncedAt = "2026-09-24T00:00:00.000Z", cursorMovies = Same.Movies, cursorEpisodes = Same.Episodes, pushedAt = "2026-09-24T00:00:01.000Z" };
        }

        [Fact]
        public void The_first_import_reads_the_whole_history()
        {
            var plan = SyncLogic.PlanRun(new SyncState(), new UserOptions { syncPull = true, syncPush = true }, false, Same);
            Assert.True(plan.Full);
            Assert.True(plan.Read);
        }

        [Fact]
        public void After_that_only_what_moved_is_read_and_nothing_at_all_when_nothing_moved()
        {
            var options = new UserOptions { syncPull = true, syncPush = true };
            Assert.False(SyncLogic.PlanRun(Synced(), options, false, Same).Read);

            var moved = SyncLogic.PlanRun(Synced(), options, false, new SyncLogic.Cursors { Movies = Same.Movies, Episodes = "2026-09-25T00:00:00.000Z" });
            Assert.True(moved.Read);
            Assert.False(moved.Full);
        }

        [Fact]
        public void A_reset_reads_the_whole_history_again()
        {
            var plan = SyncLogic.PlanRun(Synced(), new UserOptions { syncPull = true }, true, Same);
            Assert.True(plan.Full);
            Assert.True(plan.Read);
        }

        [Fact]
        public void A_user_who_only_sends_never_reads_the_whole_history_of_a_big_account()
        {
            var plan = SyncLogic.PlanRun(new SyncState(), new UserOptions { syncPull = false, syncPush = true }, false, Same);
            Assert.False(plan.Full);
            Assert.False(plan.Read);   // the first run only records a starting point
        }

        [Fact]
        public void A_send_only_user_reads_just_the_changes_once_a_starting_point_exists()
        {
            var plan = SyncLogic.PlanRun(Synced(), new UserOptions { syncPull = false, syncPush = true }, false, new SyncLogic.Cursors { Movies = "2026-09-26T00:00:00.000Z" });
            Assert.True(plan.Read);
            Assert.False(plan.Full);
        }

        [Fact]
        public void Turning_importing_on_later_does_a_full_read_then()
        {
            var state = new SyncState { syncedAt = "2026-09-24T00:00:00.000Z", pushedAt = "2026-09-24T00:00:01.000Z" };   // synced before, but never imported
            var plan = SyncLogic.PlanRun(state, new UserOptions { syncPull = true, syncPush = true }, false, Same);
            Assert.True(plan.Full);
        }
    }

    public class PushSafetyTests
    {
        [Fact]
        public void The_first_run_never_sends_history_it_only_sets_a_starting_point()
        {
            Assert.Equal(SyncLogic.PushDecision.Baseline, SyncLogic.DecidePush(false, 0));
            Assert.Equal(SyncLogic.PushDecision.Baseline, SyncLogic.DecidePush(false, 40000));
        }

        [Fact]
        public void A_handful_of_new_plays_is_sent()
        {
            Assert.Equal(SyncLogic.PushDecision.Send, SyncLogic.DecidePush(true, 1));
            Assert.Equal(SyncLogic.PushDecision.Send, SyncLogic.DecidePush(true, SyncLogic.MaxPushItems));
        }

        [Fact]
        public void Nothing_new_sends_nothing()
        {
            Assert.Equal(SyncLogic.PushDecision.Nothing, SyncLogic.DecidePush(true, 0));
        }

        [Fact]
        public void A_sudden_flood_from_emby_is_left_alone()
        {
            Assert.Equal(SyncLogic.PushDecision.OverLimit, SyncLogic.DecidePush(true, SyncLogic.MaxPushItems + 1));
            Assert.Equal(SyncLogic.PushDecision.OverLimit, SyncLogic.DecidePush(true, 30000));
        }

        [Fact]
        public void Sending_is_on_by_default_now_that_it_cannot_copy_a_history()
        {
            Assert.True(new UserOptions().syncPush);
            Assert.True(new UserOptions().syncPull);
        }
    }
}

namespace Emby.Plugin.WeTrakr.Tests
{
    public class LiveMarkSafetyTests
    {
        [Fact]
        public void A_person_marking_a_long_series_is_sent_but_a_flood_is_not()
        {
            Assert.True(Emby.Plugin.WeTrakr.Services.WatchedMarks.WithinBurstLimit(1));
            Assert.True(Emby.Plugin.WeTrakr.Services.WatchedMarks.WithinBurstLimit(400));
            Assert.True(Emby.Plugin.WeTrakr.Services.WatchedMarks.WithinBurstLimit(Emby.Plugin.WeTrakr.Services.WatchedMarks.MaxMarksPerBurst));
            Assert.False(Emby.Plugin.WeTrakr.Services.WatchedMarks.WithinBurstLimit(Emby.Plugin.WeTrakr.Services.WatchedMarks.MaxMarksPerBurst + 1));
            Assert.False(Emby.Plugin.WeTrakr.Services.WatchedMarks.WithinBurstLimit(30000));
        }
    }
}
