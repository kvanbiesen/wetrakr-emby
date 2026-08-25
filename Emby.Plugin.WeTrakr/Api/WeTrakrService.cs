using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugin.WeTrakr.Configuration;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace Emby.Plugin.WeTrakr.Api
{
    /// <summary>
    /// HTTP endpoints exposed by the plugin under /Plugins/WeTrakr/Users/{UserId}.
    /// Consumed exclusively from configPage.js in the plugin settings page. Every
    /// Emby user gets an independent WeTrakr connection — an admin picks which
    /// user's connection to manage from a dropdown on the config page, since only
    /// admins can reach Dashboard → Plugins in the first place.
    /// All endpoints require Emby admin rights.
    /// </summary>
    [Route("/Plugins/WeTrakr/Users/{UserId}/ConnectStart", "POST")]
    [Authenticated(Roles = "Admin")]
    public class ConnectStartRequest : IReturn<DeviceCodePairing>
    {
        public string UserId { get; set; }
    }

    [Route("/Plugins/WeTrakr/Users/{UserId}/Poll", "POST")]
    [Authenticated(Roles = "Admin")]
    public class PollRequest : IReturn<PollStatus>
    {
        public string UserId { get; set; }
    }

    [Route("/Plugins/WeTrakr/Users/{UserId}/Disconnect", "POST")]
    [Authenticated(Roles = "Admin")]
    public class DisconnectRequest : IReturnVoid
    {
        public string UserId { get; set; }
    }

    [Route("/Plugins/WeTrakr/Users/{UserId}/Status", "GET")]
    [Authenticated(Roles = "Admin")]
    public class GetStatusRequest : IReturn<StatusSnapshot>
    {
        public string UserId { get; set; }
    }

    [Route("/Plugins/WeTrakr/Users/{UserId}/Settings", "POST")]
    [Authenticated(Roles = "Admin")]
    public class UpdateSettingsRequest : IReturnVoid
    {
        public string UserId { get; set; }
        public bool? ScrobblePlaying { get; set; }
        public bool? ScrobbleWatched { get; set; }
        public bool? ScrobbleRatings { get; set; }
        public bool? SyncWatchedHistory { get; set; }

        /// <summary>Full replacement list when present (not merged) — the config page always sends its complete checkbox selection.</summary>
        public string[] LocationsExcluded { get; set; }
    }

    /// <summary>
    /// Plain PascalCase response for the browser — deliberately NOT the same type
    /// used to deserialize WeTrakr's own snake_case API response. Emby's outbound
    /// service-response serializer does not honor System.Text.Json
    /// [JsonPropertyName] renames, so reusing DeviceCodeResponse here silently
    /// sent "UserCode"/"VerificationUrl" to the browser instead of the
    /// "user_code"/"verification_url" it expected.
    /// </summary>
    public class DeviceCodePairing
    {
        public string UserCode { get; set; } = string.Empty;
        public string VerificationUrl { get; set; } = string.Empty;
    }

    public class PollStatus
    {
        public string Status { get; set; } = string.Empty;
        public string Username { get; set; }
    }

    public class StatusSnapshot
    {
        public bool Connected { get; set; }
        public string Username { get; set; } = string.Empty;
        public string ApiBaseUrl { get; set; } = string.Empty;
        public bool ScrobblePlaying { get; set; }
        public bool ScrobbleWatched { get; set; }
        public bool ScrobbleRatings { get; set; }
        public bool SyncWatchedHistory { get; set; }
        public string[] LocationsExcluded { get; set; } = Array.Empty<string>();
        public DateTime? LastScrobbleAt { get; set; }
        public long ScrobbleCount { get; set; }
    }

    public class WeTrakrService : IService
    {
        private readonly DeviceCodeClient _device;

        // In-memory, per-user pending pairing state. Each device code is short-lived
        // (10 min). If the admin reloads the config page mid-flow for a given user,
        // that user loses the pending code — acceptable UX, they just click Connect again.
        private static readonly ConcurrentDictionary<Guid, string> PendingDeviceCodes = new ConcurrentDictionary<Guid, string>();

        public WeTrakrService(DeviceCodeClient device)
        {
            _device = device;
        }

        /// <summary>Starts pairing: requests a user_code from WeTrakr for the given Emby user.</summary>
        public async Task<object> Post(ConnectStartRequest request)
        {
            var cfg = RequireConfig();
            var userId = ParseUserId(request.UserId);

            var code = await _device.RequestCodeAsync(cfg.ApiBaseUrl, CancellationToken.None).ConfigureAwait(false);
            if (code == null) throw new Exception("device_code_request_failed");

            PendingDeviceCodes[userId] = code.DeviceCode;

            return new DeviceCodePairing
            {
                UserCode = code.UserCode,
                VerificationUrl = code.VerificationUrl
            };
        }

        /// <summary>
        /// Polls WeTrakr for the token. Persists it to this user's plugin configuration
        /// entry on success. Returns a status object the JS page uses to drive the state
        /// machine.
        /// </summary>
        public async Task<object> Post(PollRequest request)
        {
            var cfg = RequireConfig();
            var userId = ParseUserId(request.UserId);

            string deviceCode;
            if (!PendingDeviceCodes.TryGetValue(userId, out deviceCode) || string.IsNullOrEmpty(deviceCode))
            {
                return new PollStatus { Status = "no_pending_code" };
            }

            var result = await _device.PollTokenAsync(cfg.ApiBaseUrl, deviceCode, CancellationToken.None).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(result.AccessToken))
            {
                var userConfig = GetOrCreateUser(cfg, userId);
                userConfig.WebhookToken = result.AccessToken;
                userConfig.Username = result.Username ?? string.Empty;
                Plugin.Instance.SaveConfiguration();

                string discard;
                PendingDeviceCodes.TryRemove(userId, out discard);

                return new PollStatus { Status = "connected", Username = userConfig.Username };
            }

            // Error codes from the backend: authorization_pending, expired_token, ...
            return new PollStatus { Status = result.Error ?? "unknown" };
        }

        /// <summary>Clears this user's stored webhook token. The API-side state is kept intact.</summary>
        public void Post(DisconnectRequest request)
        {
            var cfg = RequireConfig();
            var userId = ParseUserId(request.UserId);

            cfg.Users = cfg.Users.Where(u => u.UserId != userId).ToArray();
            Plugin.Instance.SaveConfiguration();

            string discard;
            PendingDeviceCodes.TryRemove(userId, out discard);
        }

        /// <summary>Returns this user's connection + settings snapshot.</summary>
        public object Get(GetStatusRequest request)
        {
            var cfg = RequireConfig();
            var userId = ParseUserId(request.UserId);

            var userConfig = cfg.Users.FirstOrDefault(u => u.UserId == userId);
            if (userConfig == null || string.IsNullOrEmpty(userConfig.WebhookToken))
            {
                return new StatusSnapshot { Connected = false, ApiBaseUrl = cfg.ApiBaseUrl };
            }

            return new StatusSnapshot
            {
                Connected = true,
                Username = userConfig.Username,
                ApiBaseUrl = cfg.ApiBaseUrl,
                ScrobblePlaying = userConfig.ScrobblePlaying,
                ScrobbleWatched = userConfig.ScrobbleWatched,
                ScrobbleRatings = userConfig.ScrobbleRatings,
                SyncWatchedHistory = userConfig.SyncWatchedHistory,
                LocationsExcluded = userConfig.LocationsExcluded ?? Array.Empty<string>(),
                LastScrobbleAt = userConfig.LastScrobbleAt,
                ScrobbleCount = userConfig.ScrobbleCount
            };
        }

        /// <summary>Updates one or more settings for this user. Unset fields are left unchanged.</summary>
        public void Post(UpdateSettingsRequest request)
        {
            var cfg = RequireConfig();
            var userId = ParseUserId(request.UserId);
            var userConfig = GetOrCreateUser(cfg, userId);

            if (request.ScrobblePlaying.HasValue) userConfig.ScrobblePlaying = request.ScrobblePlaying.Value;
            if (request.ScrobbleWatched.HasValue) userConfig.ScrobbleWatched = request.ScrobbleWatched.Value;
            if (request.ScrobbleRatings.HasValue) userConfig.ScrobbleRatings = request.ScrobbleRatings.Value;
            if (request.SyncWatchedHistory.HasValue) userConfig.SyncWatchedHistory = request.SyncWatchedHistory.Value;
            if (request.LocationsExcluded != null) userConfig.LocationsExcluded = request.LocationsExcluded;

            Plugin.Instance.SaveConfiguration();
        }

        private static PluginConfiguration RequireConfig()
        {
            var cfg = Plugin.Instance?.Configuration;
            if (cfg == null) throw new Exception("Plugin not initialized");
            return cfg;
        }

        private static Guid ParseUserId(string userId)
        {
            Guid parsed;
            if (!Guid.TryParse(userId, out parsed)) throw new Exception("invalid_user_id");
            return parsed;
        }

        private static WeTrakrUserConfig GetOrCreateUser(PluginConfiguration cfg, Guid userId)
        {
            var existing = cfg.Users.FirstOrDefault(u => u.UserId == userId);
            if (existing != null) return existing;

            existing = new WeTrakrUserConfig { UserId = userId };
            cfg.Users = cfg.Users.Concat(new[] { existing }).ToArray();
            return existing;
        }
    }
}
