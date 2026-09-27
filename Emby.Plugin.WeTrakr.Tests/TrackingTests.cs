using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Emby.Plugin.WeTrakr.Api;
using Emby.Plugin.WeTrakr.Services;
using Xunit;

namespace Emby.Plugin.WeTrakr.Tests
{
    public class TrackingBuilderTests
    {
        private static readonly DateTime When = new DateTime(2026, 9, 10, 21, 14, 0, DateTimeKind.Utc);

        private static WatchedItem Movie(string tmdb = null, string imdb = null, string tvdb = null, DateTime? at = null)
        {
            return new WatchedItem { IsEpisode = false, Ids = new IdSet { Tmdb = tmdb, Imdb = imdb, Tvdb = tvdb }, WatchedAtUtc = at ?? When };
        }

        private static WatchedItem Episode(string showTmdb, int season, int number, string showTvdb = null, DateTime? at = null)
        {
            return new WatchedItem { IsEpisode = true, ShowIds = new IdSet { Tmdb = showTmdb, Tvdb = showTvdb }, Season = season, Number = number, WatchedAtUtc = at ?? When };
        }

        private static string Json(TrackingBody body)
        {
            return JsonSerializer.Serialize(body, WeTrakrApi.JsonOptions);
        }

        [Fact]
        public void A_movie_is_sent_with_exactly_one_id_and_its_date()
        {
            int skipped;
            var body = TrackingBuilder.Build(new[] { Movie(tmdb: "155", imdb: "tt0468569") }, out skipped);
            Assert.Equal(0, skipped);
            Assert.Equal("{\"movies\":[{\"ids\":{\"tmdb\":155},\"status\":\"watched\",\"tracked_at\":\"2026-09-10T21:14:00.000Z\"}]}", Json(body));
        }

        [Fact]
        public void Movie_id_priority_is_tmdb_then_imdb_then_tvdb()
        {
            Assert.Equal("tmdb", Pick(Movie(tmdb: "1", imdb: "tt2", tvdb: "3")));
            Assert.Equal("imdb", Pick(Movie(imdb: "tt2", tvdb: "3")));
            Assert.Equal("tvdb", Pick(Movie(tvdb: "3")));
        }

        private static string Pick(WatchedItem item)
        {
            int skipped;
            var m = TrackingBuilder.Build(new[] { item }, out skipped).Movies.Single().Ids;
            return m.Tmdb != null ? "tmdb" : m.Imdb != null ? "imdb" : "tvdb";
        }

        [Fact]
        public void Episodes_are_nested_under_their_show_by_season_and_number()
        {
            int skipped;
            var body = TrackingBuilder.Build(new[]
            {
                Episode("1399", 1, 1), Episode("1399", 1, 2), Episode("1399", 2, 1), Episode("66732", 1, 4)
            }, out skipped);

            Assert.Null(body.Movies);
            Assert.Equal(2, body.Shows.Count);
            var got = body.Shows[0];
            Assert.Equal("1399", got.Ids.Tmdb);
            Assert.Equal("watching", got.Status);
            Assert.Equal(new[] { 1, 2 }, got.Seasons.Select(s => s.Number));
            Assert.Equal(new[] { 1, 2 }, got.Seasons[0].Episodes.Select(e => e.Number));
            Assert.All(got.Seasons.SelectMany(s => s.Episodes), e => Assert.Equal("watched", e.Status));
            Assert.Equal(4, TrackingBuilder.Count(body));
        }

        [Fact]
        public void The_show_is_addressed_by_tmdb_then_tvdb()
        {
            int skipped;
            var body = TrackingBuilder.Build(new[] { Episode(null, 1, 1, showTvdb: "81189") }, out skipped);
            Assert.Equal("81189", body.Shows.Single().Ids.Tvdb);
            Assert.Null(body.Shows.Single().Ids.Tmdb);
        }

        [Fact]
        public void The_same_item_twice_keeps_the_latest_date_and_counts_once()
        {
            int skipped;
            var later = When.AddDays(3);
            var body = TrackingBuilder.Build(new[] { Movie(tmdb: "9", at: When), Movie(tmdb: "9", at: later), Episode("1", 1, 1, at: When), Episode("1", 1, 1, at: later) }, out skipped);
            Assert.Equal(TrackingBuilder.FormatDate(later), body.Movies.Single().TrackedAt);
            Assert.Equal(TrackingBuilder.FormatDate(later), body.Shows.Single().Seasons.Single().Episodes.Single().TrackedAt);
            Assert.Equal(2, TrackingBuilder.Count(body));
        }

        [Fact]
        public void Items_wetrakr_cannot_address_are_counted_not_sent()
        {
            int skipped;
            var body = TrackingBuilder.Build(new[]
            {
                Movie(),                              // no id at all
                Episode(null, 1, 1),                  // show without an id
                Episode("1399", 1, 0),                // no episode number
                Movie(tmdb: "1")
            }, out skipped);
            Assert.Equal(3, skipped);
            Assert.Equal(1, TrackingBuilder.Count(body));
        }

        [Fact]
        public void Nothing_to_send_leaves_both_lists_out_of_the_body()
        {
            int skipped;
            var body = TrackingBuilder.Build(new WatchedItem[0], out skipped);
            Assert.Equal("{}", Json(body));
        }
    }

    public class TrackingBatcherTests
    {
        private static TrackingBody Movies(int count)
        {
            return new TrackingBody { Movies = Enumerable.Range(1, count).Select(i => new TrackingMovie { Ids = new IdSet { Tmdb = i.ToString() }, Status = "watched", TrackedAt = "2026-01-01T00:00:00.000Z" }).ToList() };
        }

        [Fact]
        public void More_than_the_item_limit_is_cut_into_several_calls()
        {
            var parts = TrackingBatcher.Split(Movies(12000)).ToList();
            Assert.Equal(3, parts.Count);
            Assert.All(parts, p => Assert.True(TrackingBuilder.Count(p) <= TrackingBatcher.MaxItems));
            Assert.Equal(12000, parts.Sum(p => TrackingBuilder.Count(p)));
        }

        [Fact]
        public void A_small_body_is_one_call()
        {
            Assert.Single(TrackingBatcher.Split(Movies(10)));
        }

        [Fact]
        public void An_empty_body_gives_no_calls()
        {
            Assert.Empty(TrackingBatcher.Split(new TrackingBody()));
        }

        [Fact]
        public void The_byte_estimate_also_cuts_calls_and_stays_below_one_megabyte()
        {
            foreach (var part in TrackingBatcher.Split(Movies(5000)))
            {
                Assert.True(JsonSerializer.Serialize(part, WeTrakrApi.JsonOptions).Length < 1000000);
            }
            Assert.True(TrackingBatcher.Split(Movies(4000), 5000, 100000).Count() > 1);
        }

        [Fact]
        public void A_show_split_across_calls_keeps_its_structure_in_each()
        {
            int skipped;
            var items = Enumerable.Range(1, 30).Select(n => new WatchedItem { IsEpisode = true, ShowIds = new IdSet { Tmdb = "1399" }, Season = 1 + n / 11, Number = n, WatchedAtUtc = DateTime.UtcNow }).ToList();
            var body = TrackingBuilder.Build(items, out skipped);
            var parts = TrackingBatcher.Split(body, 10, TrackingBatcher.MaxBytes).ToList();

            Assert.Equal(3, parts.Count);
            Assert.All(parts, p =>
            {
                Assert.Equal(10, TrackingBuilder.Count(p));
                Assert.Equal("1399", p.Shows.Single().Ids.Tmdb);
                Assert.Equal("watching", p.Shows.Single().Status);
            });
            Assert.Equal(30, parts.Sum(p => TrackingBuilder.Count(p)));
        }

        [Fact]
        public void Movies_and_episodes_in_one_body_are_all_kept()
        {
            int skipped;
            var body = TrackingBuilder.Build(new[]
            {
                new WatchedItem { IsEpisode = false, Ids = new IdSet { Tmdb = "1" }, WatchedAtUtc = DateTime.UtcNow },
                new WatchedItem { IsEpisode = true, ShowIds = new IdSet { Tmdb = "2" }, Season = 1, Number = 1, WatchedAtUtc = DateTime.UtcNow }
            }, out skipped);
            Assert.Equal(2, TrackingBatcher.Split(body).Sum(p => TrackingBuilder.Count(p)));
        }
    }
}
