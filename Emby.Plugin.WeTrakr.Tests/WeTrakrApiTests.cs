using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugin.WeTrakr.Api;
using Emby.Plugin.WeTrakr.Configuration;
using MediaBrowser.Common;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using Xunit;

namespace Emby.Plugin.WeTrakr.Tests
{
    /// <summary>Answers each request from a queue and records what was sent.</summary>
    public class FakeHttp : IHttpClient
    {
        public readonly Queue<(int status, string body, Dictionary<string, string> headers)> Replies = new Queue<(int, string, Dictionary<string, string>)>();
        public readonly List<(string method, HttpRequestOptions options, string body)> Requests = new List<(string, HttpRequestOptions, string)>();

        public void Reply(int status, string body = "", Dictionary<string, string> headers = null)
        {
            Replies.Enqueue((status, body, headers ?? new Dictionary<string, string>()));
        }

        private Task<HttpResponseInfo> Answer(string method, HttpRequestOptions options)
        {
            Requests.Add((method, options, options.RequestContent.ToString()));
            var next = Replies.Dequeue();
            return Task.FromResult(new HttpResponseInfo
            {
                StatusCode = (HttpStatusCode)next.status,
                Content = new MemoryStream(Encoding.UTF8.GetBytes(next.body)),
                Headers = next.headers
            });
        }

        public Task<HttpResponseInfo> GetResponse(HttpRequestOptions options) { return Answer("GET", options); }
        public Task<HttpResponseInfo> Post(HttpRequestOptions options) { return Answer("POST", options); }
        public Task<HttpResponseInfo> SendAsync(HttpRequestOptions options, string httpMethod) { return Answer(httpMethod, options); }
        public Task<Stream> Get(HttpRequestOptions options) { throw new NotSupportedException(); }
        public IDisposable GetConnectionContext(HttpRequestOptions options) { throw new NotSupportedException(); }
        public Task<string> GetTempFile(HttpRequestOptions options) { throw new NotSupportedException(); }
        public Task<HttpResponseInfo> GetTempFileResponse(HttpRequestOptions options) { throw new NotSupportedException(); }
    }

    /// <summary>Implements an Emby interface with only the members a test names; the rest answer with defaults.</summary>
    public class Stub<T> : DispatchProxy where T : class
    {
        public Func<string, object[], object> Handler = (name, args) => null;

        protected override object Invoke(MethodInfo method, object[] args)
        {
            var result = Handler(method.Name, args);
            if (result != null) return result;
            return method.ReturnType.IsValueType && method.ReturnType != typeof(void) ? Activator.CreateInstance(method.ReturnType) : null;
        }

        public static T Create(Func<string, object[], object> handler)
        {
            var proxy = Create<T, Stub<T>>();
            ((Stub<T>)(object)proxy).Handler = handler;
            return proxy;
        }
    }

    public class ApiHarness
    {
        public const long UserId = 7;

        public readonly FakeHttp Http = new FakeHttp();
        public readonly Dictionary<string, object> Settings = new Dictionary<string, object>();
        public readonly WeTrakrApi Api;

        public ApiHarness(string key = "test-app-key")
        {
            var users = Stub<IUserManager>.Create((name, args) =>
            {
                if (name == "GetTypedUserSetting") { object value; return Settings.TryGetValue((long)args[0] + "|" + (string)args[1], out value) ? value : null; }
                if (name == "SetTypedUserSetting") { Settings[(long)args[0] + "|" + (string)args[1]] = args[2]; }
                return null;
            });
            var logs = Stub<ILogManager>.Create((name, args) => name == "GetLogger" ? Stub<ILogger>.Create((n, a) => null) : null);
            var host = Stub<IApplicationHost>.Create((name, args) => name == "get_ApplicationVersion" ? new Version(4, 9, 5, 0) : null);
            Api = new WeTrakrApi(Http, users, logs, host, key);
        }

        public AuthState LoggedIn(DateTime? expires = null)
        {
            var auth = new AuthState { accessToken = "access-1", refreshToken = "refresh-1", accessExpiresAt = (expires ?? DateTime.UtcNow.AddDays(3)).ToString("o", CultureInfo.InvariantCulture), username = "kris" };
            Settings[UserId + "|" + ConfigurationFactory.AuthKey] = auth;
            return auth;
        }

        public AuthState Auth { get { return (AuthState)Settings[UserId + "|" + ConfigurationFactory.AuthKey]; } }
    }

    public class WeTrakrApiTests
    {
        private static ScrobbleBody Body() { return new ScrobbleBody { Movie = new ScrobbleMedia { Title = "The Dark Knight", Year = 2008, Ids = new IdSet { Tmdb = "155" } }, Progress = 12.5, AppVersion = ClientInfo.AppVersion }; }

        [Fact]
        public async Task A_scrobble_carries_the_app_key_version_token_and_names_emby()
        {
            var h = new ApiHarness();
            h.LoggedIn();
            h.Http.Reply(201, "{\"action\":\"start\",\"progress\":12.5}");

            var status = await h.Api.Scrobble(ApiHarness.UserId, "start", Body(), CancellationToken.None);

            Assert.Equal(201, status);
            var request = h.Http.Requests.Single();
            Assert.Equal("POST", request.method);
            Assert.Equal("https://api.wetrakr.com/scrobble/start", request.options.Url);
            Assert.Equal("test-app-key", request.options.RequestHeaders["wetrakr-api-key"]);
            Assert.Equal("1", request.options.RequestHeaders["wetrakr-api-version"]);
            Assert.Equal("Bearer access-1", request.options.RequestHeaders["Authorization"]);
            Assert.Equal("application/json", request.options.RequestContentType);
            Assert.Contains("Emby Server 4.9.5.0", request.options.UserAgent);
            Assert.StartsWith("WeTrakr-Emby/", request.options.UserAgent);

            using (var doc = JsonDocument.Parse(request.body))
            {
                var root = doc.RootElement;
                Assert.Equal(12.5, root.GetProperty("progress").GetDouble());
                Assert.Equal("The Dark Knight", root.GetProperty("movie").GetProperty("title").GetString());
                Assert.Equal(155, root.GetProperty("movie").GetProperty("ids").GetProperty("tmdb").GetInt32());
                var appVersion = root.GetProperty("app_version").GetString();
                Assert.Contains("Emby 4.9.5.0", appVersion);
                Assert.Contains("WeTrakr-Emby/", appVersion);
                Assert.False(root.TryGetProperty("show", out _));   // an unset part is left out of the body
            }
        }

        [Fact]
        public async Task Nothing_is_sent_without_an_app_key()
        {
            var h = new ApiHarness(key: "");
            h.LoggedIn();
            await Assert.ThrowsAsync<ApiKeyMissingException>(() => h.Api.Scrobble(ApiHarness.UserId, "start", Body(), CancellationToken.None));
            Assert.Empty(h.Http.Requests);
        }

        [Fact]
        public async Task A_user_who_never_connected_is_told_so_without_a_request()
        {
            var h = new ApiHarness();
            await Assert.ThrowsAsync<NotConnectedException>(() => h.Api.Scrobble(ApiHarness.UserId, "start", Body(), CancellationToken.None));
            Assert.Empty(h.Http.Requests);
        }

        [Fact]
        public async Task A_rejected_token_is_refreshed_once_and_the_call_repeated()
        {
            var h = new ApiHarness();
            h.LoggedIn();
            h.Http.Reply(401, "{\"message\":\"No authorized!\"}");
            h.Http.Reply(200, "{\"access_token\":\"access-2\",\"expires_in\":604800,\"new_refresh_token\":\"refresh-2\"}");
            h.Http.Reply(201, "{}");

            await h.Api.Scrobble(ApiHarness.UserId, "stop", Body(), CancellationToken.None);

            Assert.Equal(new[] { "/scrobble/stop", "/oauth/token/refresh", "/scrobble/stop" }, h.Http.Requests.Select(r => new Uri(r.options.Url).AbsolutePath));
            Assert.Equal("Bearer access-2", h.Http.Requests[2].options.RequestHeaders["Authorization"]);
            Assert.False(h.Http.Requests[1].options.RequestHeaders.ContainsKey("Authorization"));   // the refresh is made with the refresh token alone
            Assert.Contains("refresh-1", h.Http.Requests[1].body);
            Assert.Equal("access-2", h.Auth.accessToken);
            Assert.Equal("refresh-2", h.Auth.refreshToken);   // the rotated one is kept
        }

        [Fact]
        public async Task A_token_about_to_expire_is_refreshed_before_the_call()
        {
            var h = new ApiHarness();
            h.LoggedIn(expires: DateTime.UtcNow.AddMinutes(2));
            h.Http.Reply(200, "{\"access_token\":\"access-2\",\"expires_in\":604800,\"new_refresh_token\":\"refresh-2\"}");
            h.Http.Reply(201, "{}");

            await h.Api.Scrobble(ApiHarness.UserId, "start", Body(), CancellationToken.None);

            Assert.Equal("/oauth/token/refresh", new Uri(h.Http.Requests[0].options.Url).AbsolutePath);
            Assert.Equal("Bearer access-2", h.Http.Requests[1].options.RequestHeaders["Authorization"]);
        }

        [Fact]
        public async Task A_revoked_login_is_forgotten_and_the_user_has_to_connect_again()
        {
            var h = new ApiHarness();
            h.LoggedIn();
            h.Http.Reply(401, "{}");
            h.Http.Reply(401, "{\"message\":\"Invalid or expired refresh token\"}");

            await Assert.ThrowsAsync<NotConnectedException>(() => h.Api.Scrobble(ApiHarness.UserId, "start", Body(), CancellationToken.None));

            Assert.Equal("", h.Auth.refreshToken);
            Assert.Equal("", h.Auth.accessToken);
        }

        [Fact]
        public async Task The_daily_quota_is_reported_and_never_retried()
        {
            var h = new ApiHarness();
            h.LoggedIn();
            h.Http.Reply(429, "{\"error\":\"QUOTA_EXCEEDED\",\"message\":\"This user's free plan allows 1000 API requests per day.\"}");

            var ex = await Assert.ThrowsAsync<QuotaExceededException>(() => h.Api.GetActivities(ApiHarness.UserId, CancellationToken.None));

            Assert.Contains("1000", ex.Message);
            Assert.Single(h.Http.Requests);
        }

        [Fact]
        public async Task A_per_minute_limit_is_waited_out_for_a_sync_call()
        {
            var h = new ApiHarness();
            h.LoggedIn();
            h.Http.Reply(429, "{\"message\":\"slow down\"}", new Dictionary<string, string> { { "Retry-After", "1" } });
            h.Http.Reply(200, "{\"all\":\"2026-09-22T10:09:59.528Z\"}");

            var activities = await h.Api.GetActivities(ApiHarness.UserId, CancellationToken.None);

            Assert.Equal(2, h.Http.Requests.Count);
            Assert.Equal("2026-09-22T10:09:59.528Z", activities.All);
        }

        [Fact]
        public async Task A_scrobble_that_hits_a_limit_is_dropped_not_retried_late()
        {
            var h = new ApiHarness();
            h.LoggedIn();
            h.Http.Reply(429, "{\"message\":\"slow down\"}");

            var ex = await Assert.ThrowsAsync<WeTrakrApiException>(() => h.Api.Scrobble(ApiHarness.UserId, "start", Body(), CancellationToken.None));

            Assert.Equal(429, ex.Status);
            Assert.Single(h.Http.Requests);
        }

        [Fact]
        public async Task A_server_error_on_a_scrobble_is_not_retried_either()
        {
            var h = new ApiHarness();
            h.LoggedIn();
            h.Http.Reply(503, "");

            var ex = await Assert.ThrowsAsync<WeTrakrApiException>(() => h.Api.Scrobble(ApiHarness.UserId, "stop", Body(), CancellationToken.None));

            Assert.Equal(503, ex.Status);
            Assert.Single(h.Http.Requests);
        }

        [Fact]
        public async Task The_plan_limit_status_becomes_its_code()
        {
            var h = new ApiHarness();
            h.LoggedIn();
            h.Http.Reply(420, "{\"error\":\"PLAN_LIMIT_REACHED\",\"message\":\"Free plan is limited.\"}");
            var ex = await Assert.ThrowsAsync<WeTrakrApiException>(() => h.Api.AddTracking(ApiHarness.UserId, new TrackingBody(), CancellationToken.None));
            Assert.Equal("PLAN_LIMIT_REACHED", ex.Code);
        }

        [Fact]
        public async Task The_watched_list_is_read_oldest_first_with_from_date_and_the_page_count_header()
        {
            var h = new ApiHarness();
            h.LoggedIn();
            h.Http.Reply(200, "[{\"id\":126,\"type\":\"movie\",\"title\":\"The Dark Knight\",\"ids\":{\"tmdb\":155},\"watched_at\":\"2026-09-15T21:09:53.772Z\"}]",
                new Dictionary<string, string> { { "x-pagination-page-count", "7" } });

            var page = await h.Api.GetWatchedPage(ApiHarness.UserId, "movies", 3, "2026-09-26T09:59:00.000Z", CancellationToken.None);

            var request = h.Http.Requests.Single();
            Assert.Equal("GET", request.method);
            Assert.Equal("https://api.wetrakr.com/sync/tracking/watched/movies?limit=100&sort_by=added&sort_dir=asc&page=3&from_date=2026-09-26T09%3A59%3A00.000Z", request.options.Url);
            Assert.Equal(7, page.PageCount);
            Assert.Equal("155", page.Entries.Single().Ids.Tmdb);
        }

        [Fact]
        public async Task Without_a_page_count_header_a_full_page_means_there_may_be_more()
        {
            var h = new ApiHarness();
            h.LoggedIn();
            var full = "[" + string.Join(",", Enumerable.Range(1, 100).Select(i => "{\"id\":" + i + "}")) + "]";
            h.Http.Reply(200, full);
            Assert.Equal(2, (await h.Api.GetWatchedPage(ApiHarness.UserId, "episodes", 1, null, CancellationToken.None)).PageCount);
        }

        [Fact]
        public async Task Adding_history_posts_the_body_and_counts_what_was_not_found()
        {
            var h = new ApiHarness();
            h.LoggedIn();
            h.Http.Reply(200, "{\"added\":{\"total\":1},\"notFound\":{\"movies\":[{\"ids\":{\"imdb\":\"tt0000000\"}}],\"shows\":[],\"episodes\":[]}}");
            int skipped;
            var body = Emby.Plugin.WeTrakr.Services.TrackingBuilder.Build(new[] { new Emby.Plugin.WeTrakr.Services.WatchedItem { Ids = new IdSet { Tmdb = "155" }, WatchedAtUtc = new DateTime(2026, 9, 10, 21, 14, 0, DateTimeKind.Utc) } }, out skipped);

            var result = await h.Api.AddTracking(ApiHarness.UserId, body, CancellationToken.None);

            Assert.Equal("https://api.wetrakr.com/sync/tracking", h.Http.Requests.Single().options.Url);
            Assert.Equal("{\"movies\":[{\"ids\":{\"tmdb\":155},\"status\":\"watched\",\"tracked_at\":\"2026-09-10T21:14:00.000Z\"}]}", h.Http.Requests.Single().body);
            Assert.Equal(1, result.Added.Total);
            Assert.Equal(1, result.NotFoundCount);
        }

        // ---- login ----

        [Fact]
        public async Task A_device_code_is_requested_with_the_app_key_as_client_id()
        {
            var h = new ApiHarness();
            h.Http.Reply(200, "{\"device_code\":\"d9c1\",\"user_code\":\"K7PX4M\",\"verification_url\":\"https://wetrakr.com/activate\",\"expires_in\":600,\"interval\":5}");

            var code = await h.Api.RequestDeviceCode(CancellationToken.None);

            Assert.Equal("K7PX4M", code.UserCode);
            Assert.Equal(5, code.Interval);
            Assert.Equal("{\"client_id\":\"test-app-key\"}", h.Http.Requests.Single().body);
            Assert.False(h.Http.Requests.Single().options.RequestHeaders.ContainsKey("Authorization"));
        }

        [Theory]
        [InlineData(400, DevicePollKind.Pending)]
        [InlineData(429, DevicePollKind.SlowDown)]
        [InlineData(410, DevicePollKind.Expired)]
        [InlineData(418, DevicePollKind.Denied)]
        [InlineData(404, DevicePollKind.Invalid)]
        [InlineData(409, DevicePollKind.Invalid)]
        public async Task Each_poll_status_means_what_the_docs_say(int status, DevicePollKind expected)
        {
            var h = new ApiHarness();
            h.Http.Reply(status, "{}");
            Assert.Equal(expected, (await h.Api.PollDeviceToken("d9c1", CancellationToken.None)).Kind);
        }

        [Fact]
        public async Task An_approved_poll_returns_the_tokens_which_are_then_stored()
        {
            var h = new ApiHarness();
            h.Http.Reply(200, "{\"access_token\":\"a1\",\"token_type\":\"bearer\",\"expires_in\":604800,\"refresh_token\":\"r1\"}");

            var poll = await h.Api.PollDeviceToken("d9c1", CancellationToken.None);
            h.Api.StoreLogin(ApiHarness.UserId, poll.Token);

            Assert.Equal(DevicePollKind.Approved, poll.Kind);
            Assert.Equal("a1", h.Auth.accessToken);
            Assert.Equal("r1", h.Auth.refreshToken);
            Assert.True(DateTime.Parse(h.Auth.accessExpiresAt, null, DateTimeStyles.RoundtripKind) > DateTime.UtcNow.AddDays(6));
        }

        [Fact]
        public async Task A_rejected_app_key_at_login_is_explained()
        {
            var h = new ApiHarness();
            h.Http.Reply(401, "{\"error\":\"invalid_client\",\"message\":\"Unknown client_id.\"}");
            var ex = await Assert.ThrowsAsync<WeTrakrApiException>(() => h.Api.RequestDeviceCode(CancellationToken.None));
            Assert.Equal("invalid_client", ex.Code);
        }

        [Fact]
        public async Task Too_many_codes_from_one_address_is_explained()
        {
            var h = new ApiHarness();
            h.Http.Reply(429, "{\"error\":\"too_many_requests\"}");
            var ex = await Assert.ThrowsAsync<WeTrakrApiException>(() => h.Api.RequestDeviceCode(CancellationToken.None));
            Assert.Contains("5 login codes", ex.Message);
        }

        [Fact]
        public async Task Logging_out_revokes_the_refresh_token_and_forgets_the_login_even_if_wetrakr_is_down()
        {
            var h = new ApiHarness();
            h.LoggedIn();
            h.Http.Reply(503, "");

            await h.Api.Logout(ApiHarness.UserId, CancellationToken.None);

            Assert.Equal("/oauth/logout", new Uri(h.Http.Requests.Single().options.Url).AbsolutePath);
            Assert.Contains("refresh-1", h.Http.Requests.Single().body);
            Assert.Equal("", h.Auth.refreshToken);
            Assert.Equal("", h.Auth.username);
        }
    }
}
