using System;

namespace Emby.Plugin.WeTrakr.Configuration
{
    // Property names are lowerCamelCase on purpose: Emby's typed-setting store keeps the
    // declared names verbatim and the settings page reads and writes them as-is.

    /// <summary>What the user chose on their WeTrakr page. Written by the page only.</summary>
    public class UserOptions
    {
        public UserOptions()
        {
            scrobbleMovies = true;
            scrobbleShows = true;
            minLength = 5;
            syncPull = true;
            syncPush = true;
            autoSync = false;
            autoSyncHours = 24;
            excludedLibraries = new string[0];
        }

        public bool scrobbleMovies { get; set; }
        public bool scrobbleShows { get; set; }

        /// <summary>Items shorter than this many minutes are never reported.</summary>
        public int minLength { get; set; }

        /// <summary>A sync run marks what WeTrakr has as watched in Emby.</summary>
        public bool syncPull { get; set; }

        /// <summary>A sync run sends what Emby has as watched to WeTrakr.</summary>
        public bool syncPush { get; set; }

        /// <summary>The scheduled task runs a sync for this user on its own.</summary>
        public bool autoSync { get; set; }

        public int autoSyncHours { get; set; }

        /// <summary>Emby ids of libraries (collection folders) whose items are never sent or changed.</summary>
        public string[] excludedLibraries { get; set; }
    }

    /// <summary>The user's WeTrakr login. Written only by the server; the browser never receives the tokens.</summary>
    public class AuthState
    {
        public AuthState()
        {
            accessToken = "";
            refreshToken = "";
            accessExpiresAt = "";
            username = "";
        }

        public string accessToken { get; set; }
        public string refreshToken { get; set; }

        /// <summary>UTC, "o" format. Empty when unknown.</summary>
        public string accessExpiresAt { get; set; }

        /// <summary>WeTrakr username behind the login, for display and to spot an account switch.</summary>
        public string username { get; set; }
    }

    /// <summary>Progress and results of the history sync, kept apart from the options.</summary>
    public class SyncState
    {
        public SyncState()
        {
            accountUser = "";
            lastRunAt = "";
            lastSummary = "";
            lastAutoRunAt = "";
            fullFetchAt = "";
            syncedAt = "";
            cursorMovies = "";
            cursorEpisodes = "";
            cursorShows = "";
            pushedAt = "";
            resumeTarget = "";
            resumePage = 0;
        }

        /// <summary>WeTrakr user the cursors below were read under; a different login starts over.</summary>
        public string accountUser { get; set; }

        public string lastRunAt { get; set; }
        public string lastSummary { get; set; }

        /// <summary>When the scheduled task (or switching automatic sync on) last ran this user; gates the interval.</summary>
        public string lastAutoRunAt { get; set; }

        /// <summary>When the whole watched history was last read from WeTrakr; empty until the first full read finishes.</summary>
        public string fullFetchAt { get; set; }

        /// <summary>The "all" timestamp of last_activities read at the start of the last good run (WeTrakr's clock).</summary>
        public string syncedAt { get; set; }

        public string cursorMovies { get; set; }
        public string cursorEpisodes { get; set; }
        public string cursorShows { get; set; }

        /// <summary>End of the last run that sent Emby history; only plays newer than this are considered next time.</summary>
        public string pushedAt { get; set; }

        /// <summary>Where an interrupted full read resumes ("movies" or "episodes"), so a daily quota does not restart it.</summary>
        public string resumeTarget { get; set; }
        public int resumePage { get; set; }
    }
}
