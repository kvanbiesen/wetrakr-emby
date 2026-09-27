using System.Collections.Generic;
using System.Text.Json;
using Emby.Plugin.WeTrakr.Api;
using Xunit;

namespace Emby.Plugin.WeTrakr.Tests
{
    public class ClientInfoTests
    {
        [Fact]
        public void UserAgent_names_the_plugin_emby_and_both_versions()
        {
            var agent = ClientInfo.Build("2.0.0", "4.9.5.0");
            Assert.Equal("WeTrakr-Emby/2.0.0 (Emby Server 4.9.5.0; +https://github.com/kvanbiesen/wetrakr-emby)", agent);
        }

        [Fact]
        public void AppVersion_names_emby_and_its_version()
        {
            Assert.Equal("WeTrakr-Emby/2.0.0 (Emby 4.9.5.0)", ClientInfo.BuildAppVersion("2.0.0", "4.9.5.0"));
        }

        [Fact]
        public void Live_values_use_the_version_given_to_Init()
        {
            ClientInfo.Init(new System.Version(4, 9, 5, 0));
            Assert.Contains("Emby Server 4.9.5.0", ClientInfo.UserAgent);
            Assert.Contains("Emby 4.9.5.0", ClientInfo.AppVersion);
            Assert.StartsWith("WeTrakr-Emby/", ClientInfo.UserAgent);
        }
    }

    public class IdParsingTests
    {
        private static T Read<T>(string json) { return JsonSerializer.Deserialize<T>(json, WeTrakrApi.JsonOptions); }

        [Fact]
        public void Ids_are_read_whether_wetrakr_sends_numbers_strings_or_objects()
        {
            var flat = Read<IdSet>("{\"tmdb\":105,\"imdb\":\"tt0088763\",\"tvdb\":78901}");
            Assert.Equal("105", flat.Tmdb);
            Assert.Equal("tt0088763", flat.Imdb);
            Assert.Equal("78901", flat.Tvdb);

            var nested = Read<IdSet>("{\"tmdb\":{\"id\":155},\"imdb\":{\"id\":\"tt0468569\"}}");
            Assert.Equal("155", nested.Tmdb);
            Assert.Equal("tt0468569", nested.Imdb);
        }

        [Fact]
        public void Ids_are_written_as_numbers_when_they_are_numbers()
        {
            var json = JsonSerializer.Serialize(new IdSet { Tmdb = "155", Imdb = "tt0468569" }, WeTrakrApi.JsonOptions);
            Assert.Equal("{\"tmdb\":155,\"imdb\":\"tt0468569\"}", json);
        }

        [Fact]
        public void An_episode_entry_of_the_watched_list_is_read()
        {
            const string json = "[{\"id\":5092071,\"type\":\"episode\",\"media_id\":1333953,\"ids\":{\"tmdb\":5119022,\"imdb\":\"tt31140164\",\"tvdb\":10287742},"
                + "\"season_number\":1,\"number\":1,\"air_date\":\"2026-04-14T00:00:00.000Z\","
                + "\"show\":{\"id\":1333953,\"title\":\"Margo's Got Money Troubles\",\"ids\":{\"tmdb\":245318,\"tvdb\":445651}},\"watched_at\":\"2026-09-15T21:09:53.772Z\",\"watched_count\":2}]";
            var entries = Read<List<TrackedEntry>>(json);
            Assert.Single(entries);
            Assert.Equal(1, entries[0].SeasonNumber);
            Assert.Equal(1, entries[0].Number);
            Assert.Equal("245318", entries[0].Show.Ids.Tmdb);
            Assert.Equal("10287742", entries[0].Ids.Tvdb);
            Assert.Equal("2026-09-15T21:09:53.772Z", entries[0].WatchedAt);
        }

        [Fact]
        public void Missing_keys_are_simply_absent()
        {
            var entry = Read<List<TrackedEntry>>("[{\"id\":126,\"type\":\"movie\",\"title\":\"The Dark Knight\"}]")[0];
            Assert.Null(entry.Ids);
            Assert.Null(entry.WatchedAt);
        }
    }

    public class ErrorBodyTests
    {
        private static ErrorBody Parse(string json) { return JsonSerializer.Deserialize<ErrorBody>(json, WeTrakrApi.JsonOptions); }

        [Fact]
        public void A_string_error_is_the_code_and_message_is_the_text()
        {
            var error = Parse("{\"error\":\"QUOTA_EXCEEDED\",\"message\":\"This user's free plan allows 1000 API requests per day.\"}");
            Assert.Equal("QUOTA_EXCEEDED", error.Code);
            Assert.Contains("1000", error.Text);
        }

        [Fact]
        public void An_error_object_carries_code_and_message()
        {
            var error = Parse("{\"success\":false,\"error\":{\"code\":\"INVALID_MEDIA_ID\",\"message\":\"Bad id\"}}");
            Assert.Equal("INVALID_MEDIA_ID", error.Code);
            Assert.Equal("Bad id", error.Text);
        }

        [Fact]
        public void A_plain_message_has_no_code()
        {
            var error = Parse("{\"message\":\"No authorized!\"}");
            Assert.Null(error.Code);
            Assert.Equal("No authorized!", error.Text);
        }
    }

    public class TokenTests
    {
        [Fact]
        public void The_refresh_endpoint_names_the_new_refresh_token_differently()
        {
            var exchange = JsonSerializer.Deserialize<TokenDto>("{\"access_token\":\"a\",\"expires_in\":604800,\"refresh_token\":\"r1\"}", WeTrakrApi.JsonOptions);
            var refresh = JsonSerializer.Deserialize<TokenDto>("{\"access_token\":\"b\",\"expires_in\":604800,\"new_refresh_token\":\"r2\"}", WeTrakrApi.JsonOptions);
            Assert.Equal("r1", exchange.EffectiveRefreshToken);
            Assert.Equal("r2", refresh.EffectiveRefreshToken);
        }
    }
}
