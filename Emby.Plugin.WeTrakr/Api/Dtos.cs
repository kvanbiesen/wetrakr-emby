using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Emby.Plugin.WeTrakr.Api
{
    // Wire shapes of the WeTrakr public API (https://api.wetrakr.com). They go through
    // System.Text.Json directly: Emby's own serializer ignores [JsonPropertyName].

    /// <summary>
    /// An external id that WeTrakr sends as a number (tmdb, tvdb), a string (imdb) or,
    /// on full media objects, an object holding the id. Read into a string either way;
    /// written back as a number when it is one, as WeTrakr expects.
    /// </summary>
    public class FlexIdConverter : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.Number:
                    return reader.TryGetInt64(out var number) ? number.ToString(CultureInfo.InvariantCulture) : reader.GetDouble().ToString(CultureInfo.InvariantCulture);
                case JsonTokenType.String:
                    return reader.GetString();
                case JsonTokenType.StartObject:
                    using (var doc = JsonDocument.ParseValue(ref reader))
                    {
                        JsonElement inner;
                        if (!doc.RootElement.TryGetProperty("id", out inner)) return null;
                        return inner.ValueKind == JsonValueKind.Number ? inner.GetRawText() : inner.ValueKind == JsonValueKind.String ? inner.GetString() : null;
                    }
                case JsonTokenType.StartArray:
                    reader.Skip();
                    return null;
                default:
                    return null;
            }
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            long number;
            if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number)) writer.WriteNumberValue(number);
            else writer.WriteStringValue(value);
        }
    }

    public class IdSet
    {
        [JsonPropertyName("tmdb"), JsonConverter(typeof(FlexIdConverter))] public string Tmdb { get; set; }
        [JsonPropertyName("imdb"), JsonConverter(typeof(FlexIdConverter))] public string Imdb { get; set; }
        [JsonPropertyName("tvdb"), JsonConverter(typeof(FlexIdConverter))] public string Tvdb { get; set; }

        [JsonIgnore]
        public bool IsEmpty => string.IsNullOrEmpty(Tmdb) && string.IsNullOrEmpty(Imdb) && string.IsNullOrEmpty(Tvdb);
    }

    // ---- login ----

    public class DeviceCodeDto
    {
        [JsonPropertyName("device_code")] public string DeviceCode { get; set; }
        [JsonPropertyName("user_code")] public string UserCode { get; set; }
        [JsonPropertyName("verification_url")] public string VerificationUrl { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("interval")] public int Interval { get; set; }
    }

    public class TokenDto
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }

        // the token exchange calls it refresh_token, the refresh endpoint new_refresh_token
        [JsonPropertyName("refresh_token")] public string RefreshToken { get; set; }
        [JsonPropertyName("new_refresh_token")] public string NewRefreshToken { get; set; }

        [JsonIgnore]
        public string EffectiveRefreshToken => !string.IsNullOrEmpty(NewRefreshToken) ? NewRefreshToken : RefreshToken;
    }

    public class AccountDto
    {
        [JsonPropertyName("user")] public AccountUser User { get; set; }
        [JsonPropertyName("username")] public string Username { get; set; }

        [JsonIgnore]
        public string EffectiveUsername => User?.Username ?? Username;
    }

    public class AccountUser
    {
        [JsonPropertyName("username")] public string Username { get; set; }
    }

    // ---- scrobble ----

    public class ScrobbleMedia
    {
        [JsonPropertyName("title")] public string Title { get; set; }
        [JsonPropertyName("year")] public int? Year { get; set; }
        [JsonPropertyName("ids")] public IdSet Ids { get; set; }
    }

    public class ScrobbleEpisode
    {
        [JsonPropertyName("season")] public int Season { get; set; }
        [JsonPropertyName("number")] public int Number { get; set; }
        [JsonPropertyName("title")] public string Title { get; set; }
    }

    public class ScrobbleBody
    {
        [JsonPropertyName("movie")] public ScrobbleMedia Movie { get; set; }
        [JsonPropertyName("show")] public ScrobbleMedia Show { get; set; }
        [JsonPropertyName("episode")] public ScrobbleEpisode Episode { get; set; }
        [JsonPropertyName("progress")] public double Progress { get; set; }
        [JsonPropertyName("app_version")] public string AppVersion { get; set; }
    }

    // ---- tracking writes (POST /sync/tracking and /sync/tracking/remove) ----

    public class TrackingBody
    {
        [JsonPropertyName("movies")] public List<TrackingMovie> Movies { get; set; }
        [JsonPropertyName("shows")] public List<TrackingShow> Shows { get; set; }

        // WeTrakr also documents a top-level "episodes" field addressed by its own internal episode id, with no
        // show around it, but Emby has no way to know that id for an episode it has never round-tripped, so
        // nothing in this plugin sends one; an episode is instead always nested under its show and season, by
        // number, in Shows.
    }

    public class TrackingMovie
    {
        [JsonPropertyName("ids")] public IdSet Ids { get; set; }
        [JsonPropertyName("status")] public string Status { get; set; }
        [JsonPropertyName("tracked_at")] public string TrackedAt { get; set; }
    }

    public class TrackingShow
    {
        [JsonPropertyName("ids")] public IdSet Ids { get; set; }
        [JsonPropertyName("status")] public string Status { get; set; }
        [JsonPropertyName("seasons")] public List<TrackingSeason> Seasons { get; set; }
    }

    public class TrackingSeason
    {
        [JsonPropertyName("number")] public int Number { get; set; }
        [JsonPropertyName("episodes")] public List<TrackingEpisode> Episodes { get; set; }
    }

    public class TrackingEpisode
    {
        [JsonPropertyName("number")] public int Number { get; set; }
        [JsonPropertyName("status")] public string Status { get; set; }
        [JsonPropertyName("tracked_at")] public string TrackedAt { get; set; }
    }

    public class TrackingResult
    {
        [JsonPropertyName("added")] public CountBlock Added { get; set; }
        [JsonPropertyName("removed")] public CountBlock Removed { get; set; }

        /// <summary>Items WeTrakr could not resolve, per kind (movies, shows, seasons, episodes).</summary>
        [JsonPropertyName("notFound")] public Dictionary<string, JsonElement> NotFound { get; set; }

        [JsonIgnore]
        public int NotFoundCount
        {
            get
            {
                var count = 0;
                if (NotFound == null) return 0;
                foreach (var kind in NotFound.Values)
                {
                    if (kind.ValueKind == JsonValueKind.Array) count += kind.GetArrayLength();
                }
                return count;
            }
        }
    }

    public class CountBlock
    {
        [JsonPropertyName("total")] public int Total { get; set; }
    }

    // ---- favorites (POST /sync/favorites, POST /sync/favorites/remove, GET /sync/favorites/{target}) ----
    // Movies only: WeTrakr addresses a show, season or episode favorite by its own internal id, which
    // Emby has no way to know, so this plugin only ever sends and reads movies here.

    public class FavoritesBody
    {
        [JsonPropertyName("movies")] public List<FavoriteMovie> Movies { get; set; }
    }

    public class FavoriteMovie
    {
        [JsonPropertyName("ids")] public IdSet Ids { get; set; }
    }

    /// <summary>One entry of GET /sync/favorites/{target}: a compact media object with the user's favorite state.</summary>
    public class FavoriteEntry
    {
        [JsonPropertyName("title")] public string Title { get; set; }
        [JsonPropertyName("ids")] public IdSet Ids { get; set; }
        [JsonPropertyName("interactions")] public FavoriteInteractions Interactions { get; set; }
    }

    public class FavoriteInteractions
    {
        [JsonPropertyName("favorite")] public FavoriteValue Favorite { get; set; }
    }

    public class FavoriteValue
    {
        [JsonPropertyName("value")] public bool Value { get; set; }
    }

    // ---- reads ----

    public class ActivityBlock
    {
        [JsonPropertyName("all")] public string All { get; set; }
        [JsonPropertyName("last_tracking_watched_at")] public string LastTrackingWatchedAt { get; set; }
        [JsonPropertyName("last_tracking_removed_at")] public string LastTrackingRemovedAt { get; set; }
    }

    public class Activities
    {
        [JsonPropertyName("all")] public string All { get; set; }
        [JsonPropertyName("movies")] public ActivityBlock Movies { get; set; }
        [JsonPropertyName("shows")] public ActivityBlock Shows { get; set; }
        [JsonPropertyName("episodes")] public ActivityBlock Episodes { get; set; }
    }

    public class ShowRef
    {
        [JsonPropertyName("id")] public long? Id { get; set; }
        [JsonPropertyName("title")] public string Title { get; set; }
        [JsonPropertyName("ids")] public IdSet Ids { get; set; }
    }

    /// <summary>One entry of a tracking list: a compact movie or episode plus its watch data.</summary>
    public class TrackedEntry
    {
        [JsonPropertyName("id")] public long? Id { get; set; }
        [JsonPropertyName("type")] public string Type { get; set; }
        [JsonPropertyName("title")] public string Title { get; set; }
        [JsonPropertyName("ids")] public IdSet Ids { get; set; }
        [JsonPropertyName("season_number")] public int? SeasonNumber { get; set; }
        [JsonPropertyName("number")] public int? Number { get; set; }
        [JsonPropertyName("show")] public ShowRef Show { get; set; }
        [JsonPropertyName("watched_at")] public string WatchedAt { get; set; }
    }

    public class ErrorBody
    {
        [JsonPropertyName("message")] public string Message { get; set; }
        [JsonPropertyName("error")] public JsonElement Error { get; set; }

        /// <summary>The stable machine code (PLAN_LIMIT_REACHED, QUOTA_EXCEEDED, ...) when there is one.</summary>
        [JsonIgnore]
        public string Code
        {
            get
            {
                if (Error.ValueKind == JsonValueKind.String) return Error.GetString();
                if (Error.ValueKind == JsonValueKind.Object)
                {
                    JsonElement code;
                    if (Error.TryGetProperty("code", out code) && code.ValueKind == JsonValueKind.String) return code.GetString();
                }
                return null;
            }
        }

        [JsonIgnore]
        public string Text
        {
            get
            {
                if (!string.IsNullOrEmpty(Message)) return Message;
                if (Error.ValueKind == JsonValueKind.Object)
                {
                    JsonElement message;
                    if (Error.TryGetProperty("message", out message) && message.ValueKind == JsonValueKind.String) return message.GetString();
                }
                return null;
            }
        }
    }
}
