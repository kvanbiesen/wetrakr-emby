using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugin.WeTrakr.Configuration;
using MediaBrowser.Common;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Net;

namespace Emby.Plugin.WeTrakr.Api
{
    public class Reply
    {
        public int Status { get; set; }
        public string Body { get; set; } = string.Empty;
        public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public bool Ok => Status >= 200 && Status < 300;
    }

    public enum DevicePollKind { Pending, SlowDown, Approved, Denied, Expired, Invalid }

    public class DevicePoll
    {
        public DevicePollKind Kind { get; set; }
        public TokenDto Token { get; set; }
    }

    public class WatchedPage
    {
        public List<TrackedEntry> Entries { get; set; } = new List<TrackedEntry>();
        public int PageCount { get; set; } = 1;
    }

    public class FavoritePage
    {
        public List<FavoriteEntry> Entries { get; set; } = new List<FavoriteEntry>();
        public int PageCount { get; set; } = 1;
    }

    /// <summary>
    /// The WeTrakr public API: app key and version headers on every call, the user's
    /// OAuth token on user calls (refreshed when it is about to lapse or WeTrakr says 401),
    /// and the two limits that matter here handled in one place: a daily quota is never
    /// retried, a per-minute limit is waited out.
    /// </summary>
    public class WeTrakrApi
    {
        public const string DefaultBaseUrl = "https://api.wetrakr.com";

        private const int ScrobbleTimeoutMs = 15000;
        private const int SyncTimeoutMs = 60000;
        private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

        // one refresh per user at a time: refresh tokens rotate, and only a short grace window
        // covers the old one, so two racing refreshes could lock the user out
        private static readonly ConcurrentDictionary<long, SemaphoreSlim> RefreshGates = new ConcurrentDictionary<long, SemaphoreSlim>();

        public static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private readonly IHttpClient _http;
        private readonly IUserManager _users;
        private readonly ILogger _logger;

        private readonly string _fixedKey;

        // fixedKey is for tests; the plugin itself always takes the key from configuration or the build
        public WeTrakrApi(IHttpClient http, IUserManager users, ILogManager logManager, IApplicationHost host, string fixedKey = null)
        {
            _http = http;
            _users = users;
            _logger = logManager.GetLogger("WeTrakr API");
            _fixedKey = fixedKey;
            ClientInfo.Init(host?.ApplicationVersion);
        }

        /// <summary>The one app key compiled into this build (see BuildSecrets.ApiKey / WETRAKR_API_KEY). Never per user or per server.</summary>
        public string ApiKey => _fixedKey ?? BuildSecrets.ApiKey;

        public bool HasApiKey => !string.IsNullOrEmpty(ApiKey);

        private static string BaseUrl
        {
            get
            {
                var url = Plugin.Instance?.Configuration?.ApiBaseUrl;
                return (string.IsNullOrWhiteSpace(url) ? DefaultBaseUrl : url.Trim()).TrimEnd('/');
            }
        }

        // ---------------------------------------------------------------- login

        public async Task<DeviceCodeDto> RequestDeviceCode(CancellationToken ct)
        {
            RequireKey();
            var reply = await SendOnce("POST", "/oauth/device/code", Serialize(new Dictionary<string, string> { { "client_id", ApiKey } }), null, SyncTimeoutMs, ct).ConfigureAwait(false);
            if (reply.Ok) return Read<DeviceCodeDto>(reply);

            switch (reply.Status)
            {
                case 401: throw new WeTrakrApiException(401, "invalid_client", "WeTrakr does not recognise this plugin's app key.");
                case 423: throw new WeTrakrApiException(423, "locked_client", "WeTrakr has suspended this plugin's app key.");
                case 429: throw new WeTrakrApiException(429, "too_many_requests", "WeTrakr allows 5 login codes every 15 minutes per address. Try again in a few minutes.");
                default: throw Failure(reply);
            }
        }

        public async Task<DevicePoll> PollDeviceToken(string deviceCode, CancellationToken ct)
        {
            RequireKey();
            // client_id is documented as required here (unlike /oauth/device/code, which only needs it
            // in the header) — confirmed live that including it doesn't change the pending-state
            // response, but the approved-state exchange couldn't be tested the same way (nothing here
            // can click "approve" on wetrakr.com), so this now matches the documented contract exactly
            // rather than relying on the pending case alone to stand in for it.
            var reply = await SendOnce("POST", "/oauth/device/token", Serialize(new Dictionary<string, string> { { "device_code", deviceCode }, { "client_id", ApiKey } }), null, SyncTimeoutMs, ct).ConfigureAwait(false);
            // Logged at Info, not Debug, and including the body: this exact call is what stalled
            // silently before (a plain "400" told us nothing about whether WeTrakr meant a normal
            // "still pending" or something else), and polling only runs for the few minutes a login
            // is actually in progress, so the extra verbosity here is bounded and worth it.
            _logger.Info("WeTrakr device token poll: HTTP {0} {1}", reply.Status, Truncate(reply.Body, 300));
            switch (reply.Status)
            {
                case 200: return new DevicePoll { Kind = DevicePollKind.Approved, Token = Read<TokenDto>(reply) };
                case 400: return new DevicePoll { Kind = DevicePollKind.Pending };
                case 429: return new DevicePoll { Kind = DevicePollKind.SlowDown };
                case 410: return new DevicePoll { Kind = DevicePollKind.Expired };
                case 418: return new DevicePoll { Kind = DevicePollKind.Denied };
                case 404:
                case 409: return new DevicePoll { Kind = DevicePollKind.Invalid };
                default: throw Failure(reply);
            }
        }

        /// <summary>Stores a fresh login for the user.</summary>
        public void StoreLogin(long userId, TokenDto token)
        {
            var auth = ConfigurationFactory.LoadAuth(_users, userId);
            Apply(auth, token);
            ConfigurationFactory.SaveAuth(_users, userId, auth);
        }

        public async Task<string> GetUsername(long userId, CancellationToken ct)
        {
            var account = await GetJson<AccountDto>(userId, "/account/settings", false, ct).ConfigureAwait(false);
            return account?.EffectiveUsername ?? string.Empty;
        }

        /// <summary>Revokes the refresh token (best effort) and forgets the login.</summary>
        public async Task Logout(long userId, CancellationToken ct)
        {
            var auth = ConfigurationFactory.LoadAuth(_users, userId);
            if (!string.IsNullOrEmpty(auth.accessToken) && !string.IsNullOrEmpty(auth.refreshToken) && HasApiKey)
            {
                try
                {
                    await SendOnce("POST", "/oauth/logout", Serialize(new Dictionary<string, string> { { "refresh_token", auth.refreshToken } }), auth.accessToken, SyncTimeoutMs, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.Debug("WeTrakr logout call failed, forgetting the login anyway: {0}", ex.Message);
                }
            }
            ConfigurationFactory.SaveAuth(_users, userId, new AuthState());
        }

        // ---------------------------------------------------------------- scrobble

        /// <summary>action is start, pause or stop. Returns WeTrakr's status (201 registered, 200 kept as paused).</summary>
        public async Task<int> Scrobble(long userId, string action, ScrobbleBody body, CancellationToken ct)
        {
            var reply = await Authed(userId, "POST", "/scrobble/" + action, Serialize(body), false, ct).ConfigureAwait(false);
            EnsureOk(reply);
            return reply.Status;
        }

        // ---------------------------------------------------------------- sync

        public Task<Activities> GetActivities(long userId, CancellationToken ct)
        {
            return GetJson<Activities>(userId, "/sync/last_activities", true, ct);
        }

        /// <summary>One page of the user's watched list for "movies" or "episodes", oldest first so paging stays stable while it grows.</summary>
        public async Task<WatchedPage> GetWatchedPage(long userId, string target, int page, string fromDate, CancellationToken ct)
        {
            var path = "/sync/tracking/watched/" + target + "?limit=100&sort_by=added&sort_dir=asc&page=" + page.ToString(CultureInfo.InvariantCulture)
                + (string.IsNullOrEmpty(fromDate) ? "" : "&from_date=" + Uri.EscapeDataString(fromDate));
            var reply = await Authed(userId, "GET", path, null, true, ct).ConfigureAwait(false);
            EnsureOk(reply);

            var result = new WatchedPage { Entries = Read<List<TrackedEntry>>(reply) ?? new List<TrackedEntry>() };
            string count;
            int parsed;
            if (reply.Headers.TryGetValue("X-Pagination-Page-Count", out count) && int.TryParse(count, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) result.PageCount = Math.Max(1, parsed);
            else result.PageCount = result.Entries.Count >= 100 ? page + 1 : page;
            return result;
        }

        public Task<TrackingResult> AddTracking(long userId, TrackingBody body, CancellationToken ct)
        {
            return PostJson<TrackingResult>(userId, "/sync/tracking", body, ct);
        }

        public Task<TrackingResult> RemoveTracking(long userId, TrackingBody body, CancellationToken ct)
        {
            return PostJson<TrackingResult>(userId, "/sync/tracking/remove", body, ct);
        }

        // ---------------------------------------------------------------- favorites

        /// <summary>One page of the user's favorite movies, most recent first.</summary>
        public async Task<FavoritePage> GetFavoritesPage(long userId, int page, string fromDate, CancellationToken ct)
        {
            var path = "/sync/favorites/movies?limit=100&page=" + page.ToString(CultureInfo.InvariantCulture)
                + (string.IsNullOrEmpty(fromDate) ? "" : "&from_date=" + Uri.EscapeDataString(fromDate));
            var reply = await Authed(userId, "GET", path, null, true, ct).ConfigureAwait(false);
            EnsureOk(reply);

            var result = new FavoritePage { Entries = Read<List<FavoriteEntry>>(reply) ?? new List<FavoriteEntry>() };
            string count;
            int parsed;
            if (reply.Headers.TryGetValue("X-Pagination-Page-Count", out count) && int.TryParse(count, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) result.PageCount = Math.Max(1, parsed);
            else result.PageCount = result.Entries.Count >= 100 ? page + 1 : page;
            return result;
        }

        public Task<TrackingResult> AddFavorites(long userId, FavoritesBody body, CancellationToken ct)
        {
            return PostJson<TrackingResult>(userId, "/sync/favorites", body, ct);
        }

        public Task<TrackingResult> RemoveFavorites(long userId, FavoritesBody body, CancellationToken ct)
        {
            return PostJson<TrackingResult>(userId, "/sync/favorites/remove", body, ct);
        }

        // ---------------------------------------------------------------- plumbing

        private async Task<T> GetJson<T>(long userId, string path, bool sync, CancellationToken ct)
        {
            var reply = await Authed(userId, "GET", path, null, sync, ct).ConfigureAwait(false);
            EnsureOk(reply);
            return Read<T>(reply);
        }

        private async Task<T> PostJson<T>(long userId, string path, object body, CancellationToken ct)
        {
            var reply = await Authed(userId, "POST", path, Serialize(body), true, ct).ConfigureAwait(false);
            EnsureOk(reply);
            return Read<T>(reply);
        }

        /// <summary>
        /// A call with the user's token. sync = a long-running caller that can afford to wait
        /// (a sync run); a scrobble is never retried, a late "start" is worse than none.
        /// </summary>
        private async Task<Reply> Authed(long userId, string method, string path, string json, bool sync, CancellationToken ct)
        {
            RequireKey();
            var auth = ConfigurationFactory.LoadAuth(_users, userId);
            if (string.IsNullOrEmpty(auth.refreshToken)) throw new NotConnectedException("Not connected to WeTrakr.");
            if (Expiring(auth)) auth = await Refresh(userId, auth, ct).ConfigureAwait(false);

            // WeTrakr resolves titles by id (tmdb/imdb/tvdb) and "skips silently" — no error — when
            // it can't match one, per its own docs. That makes a mismatched or missing id on our side
            // and a genuine failure on WeTrakr's side look identical unless the actual bodies are
            // visible. A scrobble is a handful of calls per playback (WeTrakr wants events, not a
            // heartbeat) so those are worth an Info line; a sync run can be dozens of paginated
            // calls, so those only get logged at Debug to keep the normal log quiet.
            if (json != null) Log(sync, "WeTrakr {0} {1}: {2}", method, path, Truncate(json, 300));

            var timeout = sync ? SyncTimeoutMs : ScrobbleTimeoutMs;
            var retries = sync ? 2 : 0;
            var attempt = 0;
            var refreshed = false;

            while (true)
            {
                var reply = await SendOnce(method, path, json, auth.accessToken, timeout, ct).ConfigureAwait(false);

                if (reply.Status == 401)
                {
                    if (refreshed) throw ForgetLogin(userId);
                    auth = await Refresh(userId, auth, ct).ConfigureAwait(false);
                    refreshed = true;
                    continue;
                }

                if (reply.Status == 429)
                {
                    var error = ParseError(reply);
                    if (error.Code == "QUOTA_EXCEEDED") throw new QuotaExceededException(error.Text ?? "WeTrakr's daily request limit for this account is used up.");
                    if (attempt >= retries) throw new WeTrakrApiException(429, error.Code ?? "rate_limited", error.Text ?? "WeTrakr is rate limiting requests. Try again in a minute.");
                    attempt++;
                    await Task.Delay(RetryAfter(reply, TimeSpan.FromSeconds(20 * attempt)), ct).ConfigureAwait(false);
                    continue;
                }

                if (IsTransient(reply.Status) && attempt < retries)
                {
                    attempt++;
                    await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                    continue;
                }

                Log(sync, "WeTrakr {0} {1} answered HTTP {2}: {3}", method, path, reply.Status, Truncate(reply.Body, 300));
                return reply;
            }
        }

        // scrobble calls are rare (events, not a heartbeat - WeTrakr suspends keys that poll) so
        // they're logged at Info; sync calls can be dozens per run and only go to Debug.
        private void Log(bool sync, string format, params object[] args)
        {
            if (sync) _logger.Debug(format, args); else _logger.Info(format, args);
        }

        private async Task<AuthState> Refresh(long userId, AuthState stale, CancellationToken ct)
        {
            var gate = RefreshGates.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var current = ConfigurationFactory.LoadAuth(_users, userId);
                if (string.IsNullOrEmpty(current.refreshToken)) throw new NotConnectedException("Not connected to WeTrakr.");
                // another caller refreshed while this one waited its turn: use theirs
                if (!string.IsNullOrEmpty(current.accessToken) && current.accessToken != stale.accessToken) return current;

                var reply = await SendOnce("POST", "/oauth/token/refresh", Serialize(new Dictionary<string, string> { { "refresh_token", current.refreshToken } }), null, SyncTimeoutMs, ct).ConfigureAwait(false);
                if (reply.Ok)
                {
                    Apply(current, Read<TokenDto>(reply));
                    ConfigurationFactory.SaveAuth(_users, userId, current);
                    return current;
                }

                if (reply.Status == 400 || reply.Status == 401) throw ForgetLogin(userId);
                throw Failure(reply);
            }
            finally
            {
                gate.Release();
            }
        }

        private NotConnectedException ForgetLogin(long userId)
        {
            _logger.Info("WeTrakr rejected the login of Emby user {0}; it was cleared, the user has to connect again", userId);
            var auth = ConfigurationFactory.LoadAuth(_users, userId);
            auth.accessToken = "";
            auth.refreshToken = "";
            auth.accessExpiresAt = "";
            ConfigurationFactory.SaveAuth(_users, userId, auth);
            return new NotConnectedException("The WeTrakr login expired or was revoked. Connect again.");
        }

        private async Task<Reply> SendOnce(string method, string path, string json, string bearer, int timeoutMs, CancellationToken ct)
        {
            var options = new HttpRequestOptions
            {
                Url = BaseUrl + path,
                UserAgent = ClientInfo.UserAgent,
                AcceptHeader = "application/json",
                TimeoutMs = timeoutMs,
                ThrowOnErrorResponse = false,
                CancellationToken = ct
            };
            options.RequestHeaders["wetrakr-api-key"] = ApiKey;
            options.RequestHeaders["wetrakr-api-version"] = "1";
            if (!string.IsNullOrEmpty(bearer)) options.RequestHeaders["Authorization"] = "Bearer " + bearer;
            if (json != null)
            {
                options.RequestContentType = "application/json";
                options.RequestContent = json.AsMemory();
            }

            HttpResponseInfo info;
            try
            {
                info = method == "GET" ? await _http.GetResponse(options).ConfigureAwait(false) : await _http.Post(options).ConfigureAwait(false);
            }
            catch (HttpException ex)
            {
                // a status-less failure (DNS, refused, TLS, timeout) has no code to show
                throw new WeTrakrApiException(ex.StatusCode.HasValue ? (int?)ex.StatusCode.Value : null, "unreachable",
                    ex.StatusCode.HasValue ? "WeTrakr answered HTTP " + (int)ex.StatusCode.Value + "." : "WeTrakr did not answer (timed out or unreachable): " + ex.Message, ex);
            }

            using (info)
            {
                var reply = new Reply { Status = (int)info.StatusCode };
                if (info.Headers != null) reply.Headers = new Dictionary<string, string>(info.Headers, StringComparer.OrdinalIgnoreCase);
                if (info.Content != null)
                {
                    using (var reader = new StreamReader(info.Content, Encoding.UTF8))
                    {
                        reply.Body = await reader.ReadToEndAsync().ConfigureAwait(false);
                    }
                }
                return reply;
            }
        }

        private void RequireKey()
        {
            if (!HasApiKey) throw new ApiKeyMissingException();
        }

        private static bool Expiring(AuthState auth)
        {
            DateTime expires;
            if (string.IsNullOrEmpty(auth.accessToken)) return true;
            if (!DateTime.TryParse(auth.accessExpiresAt, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out expires)) return false;
            return DateTime.UtcNow + RefreshMargin >= expires;
        }

        private static void Apply(AuthState auth, TokenDto token)
        {
            if (token == null || string.IsNullOrEmpty(token.AccessToken)) throw new WeTrakrApiException(null, "bad_token", "WeTrakr sent no access token.");
            auth.accessToken = token.AccessToken;
            if (!string.IsNullOrEmpty(token.EffectiveRefreshToken)) auth.refreshToken = token.EffectiveRefreshToken;
            var lifetime = token.ExpiresIn > 0 ? token.ExpiresIn : 7 * 24 * 3600;
            auth.accessExpiresAt = DateTime.UtcNow.AddSeconds(lifetime).ToString("o", CultureInfo.InvariantCulture);
        }

        private static bool IsTransient(int status)
        {
            return status == 502 || status == 503 || status == 504 || status == 520 || status == 521 || status == 522;
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength) return value ?? string.Empty;
            return value.Substring(0, maxLength) + "…";
        }

        private static TimeSpan RetryAfter(Reply reply, TimeSpan fallback)
        {
            string value;
            int seconds;
            if (reply.Headers.TryGetValue("Retry-After", out value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds) && seconds > 0)
            {
                return TimeSpan.FromSeconds(Math.Min(seconds, 90));
            }
            return fallback;
        }

        private static void EnsureOk(Reply reply)
        {
            if (!reply.Ok) throw Failure(reply);
        }

        private static WeTrakrApiException Failure(Reply reply)
        {
            var error = ParseError(reply);
            var code = error.Code ?? (reply.Status == 420 ? "PLAN_LIMIT_REACHED" : "http_" + reply.Status);
            var text = error.Text ?? "WeTrakr answered HTTP " + reply.Status + ".";
            return new WeTrakrApiException(reply.Status, code, text);
        }

        private static ErrorBody ParseError(Reply reply)
        {
            try
            {
                return string.IsNullOrWhiteSpace(reply.Body) ? new ErrorBody() : JsonSerializer.Deserialize<ErrorBody>(reply.Body, JsonOptions) ?? new ErrorBody();
            }
            catch (JsonException)
            {
                return new ErrorBody();
            }
        }

        private static string Serialize(object body)
        {
            return JsonSerializer.Serialize(body, body.GetType(), JsonOptions);
        }

        private static T Read<T>(Reply reply)
        {
            if (string.IsNullOrWhiteSpace(reply.Body)) return default(T);
            try
            {
                return JsonSerializer.Deserialize<T>(reply.Body, JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new WeTrakrApiException(reply.Status, "bad_response", "WeTrakr sent a response this plugin could not read: " + ex.Message, ex);
            }
        }
    }
}
