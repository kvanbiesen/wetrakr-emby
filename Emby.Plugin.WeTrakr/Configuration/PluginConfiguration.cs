using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Plugins;

namespace Emby.Plugin.WeTrakr.Configuration
{
    /// <summary>
    /// Server-wide settings. Everything a user chooses (options, login, sync state)
    /// lives in Emby's per-user typed settings instead, see <see cref="ConfigurationFactory"/>.
    /// </summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        public PluginConfiguration()
        {
            ApiBaseUrl = "https://api.wetrakr.com";
        }

        /// <summary>Base URL of the WeTrakr API. Only changed to point at a test server.</summary>
        public string ApiBaseUrl { get; set; }
    }

    /// <summary>
    /// Registers the per-user stores with Emby. Three keys on purpose: the page rewrites
    /// the options whole on every save, so the login and the sync progress, which only
    /// the server writes, sit under their own keys and can never be reverted by a save.
    /// </summary>
    public class ConfigurationFactory : IUserConfigurationFactory
    {
        public const string OptionsKey = "wetrakr";
        public const string AuthKey = "wetrakrAuth";
        public const string SyncKey = "wetrakrSync";

        public IEnumerable<ConfigurationStore> GetConfigurations()
        {
            return new[]
            {
                new ConfigurationStore { ConfigurationType = typeof(UserOptions), Key = OptionsKey },
                new ConfigurationStore { ConfigurationType = typeof(AuthState), Key = AuthKey },
                new ConfigurationStore { ConfigurationType = typeof(SyncState), Key = SyncKey }
            };
        }

        public static UserOptions LoadOptions(IUserManager users, long embyUserId)
        {
            return (UserOptions)users.GetTypedUserSetting(embyUserId, OptionsKey) ?? new UserOptions();
        }

        public static AuthState LoadAuth(IUserManager users, long embyUserId)
        {
            return (AuthState)users.GetTypedUserSetting(embyUserId, AuthKey) ?? new AuthState();
        }

        public static SyncState LoadSync(IUserManager users, long embyUserId)
        {
            return (SyncState)users.GetTypedUserSetting(embyUserId, SyncKey) ?? new SyncState();
        }

        public static void SaveAuth(IUserManager users, long embyUserId, AuthState value)
        {
            users.SetTypedUserSetting(embyUserId, AuthKey, value);
        }

        public static void SaveSync(IUserManager users, long embyUserId, SyncState value)
        {
            users.SetTypedUserSetting(embyUserId, SyncKey, value);
        }

        /// <summary>True when the user has a stored login (it may still turn out to be revoked).</summary>
        public static bool IsConnected(IUserManager users, long embyUserId)
        {
            return !string.IsNullOrEmpty(LoadAuth(users, embyUserId).refreshToken);
        }

        public static User FindUser(IUserManager users, long embyUserId)
        {
            return users.GetUserById(embyUserId);
        }
    }
}
