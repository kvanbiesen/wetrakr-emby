using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugin.WeTrakr.Configuration;
using Emby.Plugin.WeTrakr.Services;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Net;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Services;
using MediaBrowser.Common;

namespace Emby.Plugin.WeTrakr.Api
{
    // The shapes below are what the pages read, so their names are lowerCamelCase and kept verbatim.

    [Route("/WeTrakr/oauth/{userId}/start", "POST")]
    [Authenticated]
    public class StartLogin : IReturn<LoginCode>
    {
        public string userId { get; set; }
    }

    [Route("/WeTrakr/oauth/{userId}/poll", "POST")]
    [Authenticated]
    public class PollLogin : IReturn<LoginStatus>
    {
        public string userId { get; set; }
    }

    [Route("/WeTrakr/oauth/{userId}/cancel", "POST")]
    [Authenticated]
    public class CancelLogin : IReturnVoid
    {
        public string userId { get; set; }
    }

    [Route("/WeTrakr/oauth/{userId}/logout", "POST")]
    [Authenticated]
    public class Logout : IReturnVoid
    {
        public string userId { get; set; }
    }

    [Route("/WeTrakr/account/{userId}", "GET")]
    [Authenticated]
    public class GetAccount : IReturn<AccountInfo>
    {
        public string userId { get; set; }
    }

    [Route("/WeTrakr/libraries/{userId}", "GET")]
    [Authenticated]
    public class GetLibraries : IReturn<List<LibraryInfo>>
    {
        public string userId { get; set; }
    }

    [Route("/WeTrakr/sync/{userId}", "POST")]
    [Authenticated]
    public class StartSync : IReturn<SyncStatus>
    {
        public string userId { get; set; }
        public bool reset { get; set; }

        /// <summary>true when switching automatic sync on: the run then counts as the first automatic one.</summary>
        public bool automatic { get; set; }
    }

    [Route("/WeTrakr/sync/{userId}/status", "GET")]
    [Authenticated]
    public class GetSyncStatus : IReturn<SyncStatus>
    {
        public string userId { get; set; }
    }

    [Route("/WeTrakr/admin", "GET")]
    [Authenticated(Roles = "Admin")]
    public class GetAdmin : IReturn<AdminInfo>
    {
    }

    [Route("/WeTrakr/admin/users", "GET")]
    [Authenticated(Roles = "Admin")]
    public class GetAdminUsers : IReturn<List<AdminUser>>
    {
    }

    public class LoginCode
    {
        public string userCode { get; set; }
        public string verificationUrl { get; set; }
        public string activateUrl { get; set; }
        public int expiresIn { get; set; }
        public int interval { get; set; }
        public string error { get; set; }
    }

    public class LoginStatus
    {
        /// <summary>pending, connected, denied, expired, none or error</summary>
        public string status { get; set; }
        public string username { get; set; }
        public string error { get; set; }
    }

    public class AccountInfo
    {
        public bool connected { get; set; }
        public string username { get; set; }
        public bool apiKeyConfigured { get; set; }
    }

    public class LibraryInfo
    {
        public string id { get; set; }
        public string name { get; set; }
    }

    public class AdminInfo
    {
        public bool apiKeyConfigured { get; set; }
        public string pluginVersion { get; set; }
        public string embyVersion { get; set; }
    }

    public class AdminUser
    {
        public string id { get; set; }
        public string name { get; set; }
        public bool connected { get; set; }
        public string username { get; set; }
        public bool autoSync { get; set; }
        public string lastRunAt { get; set; }
    }

    public class ServerEndpoint : IService, IRequiresRequest
    {
        private class Pending
        {
            public string DeviceCode;
            public DateTime ExpiresUtc;

            /// <summary>WeTrakr's own minimum gap between polls of this code, from the device-code response.</summary>
            public int IntervalSeconds;

            /// <summary>Enforced here too, not just by the browser's timer: a poll before this time is answered "pending" without calling WeTrakr at all.</summary>
            public DateTime NextPollUtc;
        }

        // the login in progress per Emby user. It lives for the 10 minutes WeTrakr gives a code, so a
        // page reload only costs the user a new code
        private static readonly ConcurrentDictionary<long, Pending> Pendings = new ConcurrentDictionary<long, Pending>();

        private readonly WeTrakrApi _api;
        private readonly HistorySync _sync;
        private readonly IAuthorizationContext _auth;
        private readonly IUserManager _users;
        private readonly ILibraryManager _library;
        private readonly ILogger _logger;

        public IRequest Request { get; set; }

        public ServerEndpoint(IHttpClient http, IAuthorizationContext auth, IUserManager users, ILibraryManager library, HistorySync sync, ILogManager logManager, IApplicationHost host)
        {
            _auth = auth;
            _users = users;
            _library = library;
            _sync = sync;
            _logger = logManager.GetLogger("WeTrakr Login");
            _api = new WeTrakrApi(http, users, logManager, host);
        }

        // ---------------------------------------------------------------- login

        public async Task<object> Post(StartLogin request)
        {
            var user = AuthorizedTarget(request.userId);
            try
            {
                var code = await _api.RequestDeviceCode(CancellationToken.None).ConfigureAwait(false);
                var interval = code.Interval > 0 ? code.Interval : 5;
                Pendings[user.InternalId] = new Pending
                {
                    DeviceCode = code.DeviceCode,
                    ExpiresUtc = DateTime.UtcNow.AddSeconds(code.ExpiresIn > 0 ? code.ExpiresIn : 600),
                    IntervalSeconds = interval,
                    NextPollUtc = DateTime.UtcNow.AddSeconds(interval)   // WeTrakr needs a moment before the first poll too
                };
                _logger.Info("WeTrakr login started for {0}: user_code {1}, interval {2}s", user.Name, code.UserCode, interval);
                var url = string.IsNullOrEmpty(code.VerificationUrl) ? "https://wetrakr.com/activate" : code.VerificationUrl;
                return new LoginCode
                {
                    userCode = code.UserCode,
                    verificationUrl = url,
                    activateUrl = url + (url.Contains("?") ? "&" : "?") + "code=" + Uri.EscapeDataString(code.UserCode ?? ""),   // the code fills itself in
                    expiresIn = code.ExpiresIn > 0 ? code.ExpiresIn : 600,
                    interval = code.Interval > 0 ? code.Interval : 5
                };
            }
            catch (WeTrakrApiException ex)
            {
                return new LoginCode { error = ex.Message };
            }
        }

        public async Task<object> Post(PollLogin request)
        {
            var user = AuthorizedTarget(request.userId);
            Pending pending;
            if (!Pendings.TryGetValue(user.InternalId, out pending)) return new LoginStatus { status = "none" };
            if (DateTime.UtcNow > pending.ExpiresUtc)
            {
                Pendings.TryRemove(user.InternalId, out pending);
                _logger.Info("WeTrakr login for {0} expired without being approved", user.Name);
                return new LoginStatus { status = "expired" };
            }

            // Enforced here too, not just by the browser's setTimeout: a reload, a second tab, or
            // simple clock drift could otherwise poll WeTrakr faster than its own interval, and
            // WeTrakr answers that with 429 (SlowDown) forever rather than ever approving — which
            // looks exactly like a stuck page, with nothing in the log to explain it. Answering
            // "pending" locally when we're not due yet avoids ever sending that request at all.
            if (DateTime.UtcNow < pending.NextPollUtc) return new LoginStatus { status = "pending" };

            try
            {
                var poll = await _api.PollDeviceToken(pending.DeviceCode, CancellationToken.None).ConfigureAwait(false);
                switch (poll.Kind)
                {
                    case DevicePollKind.Approved:
                        Pendings.TryRemove(user.InternalId, out pending);
                        _logger.Info("WeTrakr login approved for {0}", user.Name);
                        return await CompleteLogin(user, poll.Token).ConfigureAwait(false);
                    case DevicePollKind.Denied:
                        Pendings.TryRemove(user.InternalId, out pending);
                        _logger.Info("WeTrakr login denied by {0} on wetrakr.com", user.Name);
                        return new LoginStatus { status = "denied" };
                    case DevicePollKind.Expired:
                    case DevicePollKind.Invalid:
                        Pendings.TryRemove(user.InternalId, out pending);
                        _logger.Info("WeTrakr login for {0} is no longer valid ({1})", user.Name, poll.Kind);
                        return new LoginStatus { status = "expired" };
                    case DevicePollKind.SlowDown:
                        // WeTrakr's own "you polled faster than interval, wait longer": back off well
                        // past the interval it originally gave us, since that one was apparently not
                        // enough, rather than immediately trying again at the same cadence and risking
                        // this repeating (silently) until the code just expires.
                        pending.NextPollUtc = DateTime.UtcNow.AddSeconds(Math.Max(pending.IntervalSeconds * 3, 15));
                        _logger.Warn("WeTrakr rate-limited the login poll for {0}; backing off to {1}s", user.Name, Math.Max(pending.IntervalSeconds * 3, 15));
                        return new LoginStatus { status = "pending" };
                    default:
                        pending.NextPollUtc = DateTime.UtcNow.AddSeconds(pending.IntervalSeconds);
                        return new LoginStatus { status = "pending" };
                }
            }
            catch (WeTrakrApiException ex)
            {
                _logger.Warn("WeTrakr login poll for {0} failed: {1} ({2})", user.Name, ex.Message, ex.Code);
                return new LoginStatus { status = "error", error = ex.Message };
            }
        }

        private async Task<LoginStatus> CompleteLogin(User user, TokenDto token)
        {
            _api.StoreLogin(user.InternalId, token);

            var username = string.Empty;
            try
            {
                username = await _api.GetUsername(user.InternalId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (WeTrakrApiException)
            {
                // the login itself worked; the name is only for display and is read again on a later run
            }

            var auth = ConfigurationFactory.LoadAuth(_users, user.InternalId);
            auth.username = username;
            ConfigurationFactory.SaveAuth(_users, user.InternalId, auth);
            return new LoginStatus { status = "connected", username = username };
        }

        public void Post(CancelLogin request)
        {
            var user = AuthorizedTarget(request.userId);
            Pending discard;
            Pendings.TryRemove(user.InternalId, out discard);
        }

        public async Task Post(Logout request)
        {
            var user = AuthorizedTarget(request.userId);
            Pending discard;
            Pendings.TryRemove(user.InternalId, out discard);
            await _api.Logout(user.InternalId, CancellationToken.None).ConfigureAwait(false);
        }

        // ---------------------------------------------------------------- page data

        // local data only: reading the account must not spend a request of the user's daily quota
        public AccountInfo Get(GetAccount request)
        {
            var user = AuthorizedTarget(request.userId);
            var auth = ConfigurationFactory.LoadAuth(_users, user.InternalId);
            return new AccountInfo { connected = !string.IsNullOrEmpty(auth.refreshToken), username = auth.username, apiKeyConfigured = _api.HasApiKey };
        }

        public List<LibraryInfo> Get(GetLibraries request)
        {
            var user = AuthorizedTarget(request.userId);
            return ExclusionFilter.Libraries(_library, user).Select(f => new LibraryInfo { id = f.ItemId, name = f.Name }).ToList();
        }

        // ---------------------------------------------------------------- sync

        public SyncStatus Post(StartSync request)
        {
            return _sync.Start(AuthorizedTarget(request.userId), request.reset, request.automatic);
        }

        public SyncStatus Get(GetSyncStatus request)
        {
            return _sync.Status(AuthorizedTarget(request.userId));
        }

        // ---------------------------------------------------------------- admin

        public AdminInfo Get(GetAdmin request)
        {
            return Admin();
        }

        public List<AdminUser> Get(GetAdminUsers request)
        {
            var result = new List<AdminUser>();
            foreach (var user in _users.GetUserList(new UserQuery { IsDisabled = false }))
            {
                var auth = ConfigurationFactory.LoadAuth(_users, user.InternalId);
                result.Add(new AdminUser
                {
                    id = _users.GetGuid(user.InternalId).ToString("N"),
                    name = user.Name,
                    connected = !string.IsNullOrEmpty(auth.refreshToken),
                    username = auth.username,
                    autoSync = ConfigurationFactory.LoadOptions(_users, user.InternalId).autoSync,
                    lastRunAt = ConfigurationFactory.LoadSync(_users, user.InternalId).lastRunAt
                });
            }
            return result;
        }

        private static AdminInfo Admin()
        {
            return new AdminInfo
            {
                apiKeyConfigured = !string.IsNullOrEmpty(BuildSecrets.ApiKey),
                pluginVersion = ClientInfo.PluginVersion,
                embyVersion = ClientInfo.EmbyVersion
            };
        }

        // ---------------------------------------------------------------- access

        /// <summary>The caller may act on their own WeTrakr settings; an administrator on anyone's.</summary>
        private User AuthorizedTarget(string userId)
        {
            var caller = _auth.GetAuthorizationInfo(Request).User;
            if (caller == null) throw new SecurityException("A user session is required", SecurityExceptionType.Unauthenticated);

            Guid id;
            var target = Guid.TryParse(userId, out id) ? _users.GetUserById(id) : null;
            if (target == null) throw new ResourceNotFoundException("User not found");
            if (target.InternalId != caller.InternalId && !caller.Policy.IsAdministrator)
            {
                throw new SecurityException("Only the user or an administrator can change these settings", SecurityExceptionType.Generic);
            }
            return target;
        }
    }
}
