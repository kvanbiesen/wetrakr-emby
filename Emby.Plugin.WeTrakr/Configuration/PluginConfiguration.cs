using System;
using MediaBrowser.Model.Plugins;

namespace Emby.Plugin.WeTrakr.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        public PluginConfiguration()
        {
            ApiBaseUrl = "https://api.wetrakr.com";
            Users = Array.Empty<WeTrakrUserConfig>();
        }

        /// <summary>
        /// Base URL of the WeTrakr API. Default: https://api.wetrakr.com. Advanced users
        /// who self-host WeTrakr can override this. Shared across all paired users.
        /// </summary>
        public string ApiBaseUrl { get; set; }

        /// <summary>
        /// One entry per Emby user who has paired their own WeTrakr account. Emby is
        /// multi-user per server, so scrobbling is opt-in per user rather than a single
        /// server-wide connection — otherwise every household member's playback would
        /// be sent to whichever one account an admin happened to pair first.
        /// </summary>
        public WeTrakrUserConfig[] Users { get; set; }
    }

    public class WeTrakrUserConfig
    {
        public WeTrakrUserConfig()
        {
            WebhookToken = string.Empty;
            Username = string.Empty;
            ScrobblePlaying = true;
            ScrobbleWatched = true;
            ScrobbleRatings = true;
            SyncWatchedHistory = true;
            LocationsExcluded = Array.Empty<string>();
            LastScrobbleAt = null;
            ScrobbleCount = 0;
        }

        /// <summary>The Emby user this connection belongs to.</summary>
        public Guid UserId { get; set; }

        /// <summary>
        /// Token issued by the WeTrakr device-code flow. Used as the path segment when
        /// POSTing to /webhooks/jellyfin/{WebhookToken}. Empty when this user hasn't
        /// paired yet.
        /// </summary>
        public string WebhookToken { get; set; }

        /// <summary>
        /// Display name of the WeTrakr user this Emby user is paired with. Shown in the
        /// config page "Connected as" label. Not used in auth.
        /// </summary>
        public string Username { get; set; }

        /// <summary>Send PlaybackStart / Progress / Pause / Unpause / Stop events.</summary>
        public bool ScrobblePlaying { get; set; }

        /// <summary>Send ItemMarkedPlayed events (reserved for plugin v2).</summary>
        public bool ScrobbleWatched { get; set; }

        /// <summary>Send UserDataSaved (ratings/favorites) events (reserved for plugin v3).</summary>
        public bool ScrobbleRatings { get; set; }

        /// <summary>
        /// Whether the "Sync WeTrakr watched history" scheduled task should push this
        /// user's already-watched library items to WeTrakr. Separate from
        /// ScrobbleWatched, which covers live "mark as watched" toggles — this covers
        /// bulk backfill of pre-existing watched status.
        /// </summary>
        public bool SyncWatchedHistory { get; set; }

        /// <summary>
        /// Library folder paths excluded from the watched-history sync scheduled task,
        /// e.g. home videos the user doesn't want reflected on WeTrakr. Matches Trakt's
        /// LocationsExcluded — a top-level media folder path per entry.
        /// </summary>
        public string[] LocationsExcluded { get; set; }

        public DateTime? LastScrobbleAt { get; set; }

        public long ScrobbleCount { get; set; }
    }
}
