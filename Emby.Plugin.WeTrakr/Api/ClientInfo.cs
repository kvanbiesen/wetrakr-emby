using System;

namespace Emby.Plugin.WeTrakr.Api
{
    /// <summary>
    /// How this plugin introduces itself to WeTrakr: the User-Agent of every request and
    /// the app_version of every scrobble both name Emby and its version, so WeTrakr
    /// support can tell which server and plugin build a report came from. WeTrakr also
    /// needs a real User-Agent: its edge rejects default library agents with a 403.
    /// </summary>
    public static class ClientInfo
    {
        public const string AppName = "WeTrakr-Emby";
        private const string ProjectUrl = "https://github.com/kvanbiesen/wetrakr-emby";

        private static string _embyVersion = "unknown";

        public static string PluginVersion { get; } = typeof(ClientInfo).Assembly.GetName().Version.ToString(3);

        public static string EmbyVersion => _embyVersion;

        /// <summary>Called by anything that has the application host; harmless to call again.</summary>
        public static void Init(Version embyVersion)
        {
            if (embyVersion != null) _embyVersion = embyVersion.ToString();
        }

        /// <summary>e.g. "WeTrakr-Emby/2.0.0 (Emby Server 4.9.5.0; +https://github.com/kvanbiesen/wetrakr-emby)"</summary>
        public static string UserAgent => Build(PluginVersion, _embyVersion);

        /// <summary>e.g. "WeTrakr-Emby/2.0.0 (Emby 4.9.5.0)". The scrobble body's free-text app_version.</summary>
        public static string AppVersion => BuildAppVersion(PluginVersion, _embyVersion);

        public static string Build(string pluginVersion, string embyVersion)
        {
            return AppName + "/" + pluginVersion + " (Emby Server " + embyVersion + "; +" + ProjectUrl + ")";
        }

        public static string BuildAppVersion(string pluginVersion, string embyVersion)
        {
            return AppName + "/" + pluginVersion + " (Emby " + embyVersion + ")";
        }
    }
}
