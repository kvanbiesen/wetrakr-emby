using System;
using System.Collections.Generic;
using System.IO;
using Emby.Plugin.WeTrakr.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Emby.Plugin.WeTrakr
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage
    {
        /// <summary>Id of the per-user feature, so an admin can switch WeTrakr off for a user.</summary>
        public const string StaticId = "WeTrakr";
        public const string StaticName = "WeTrakr";

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        public static Plugin Instance { get; private set; }

        private readonly Guid _id = new Guid("f473634f-caf9-4a27-9323-2813fa0e0eaf");

        public override Guid Id => _id;

        public override string Name => "WeTrakr";

        public override string Description =>
            "Scrobble what you watch to WeTrakr and keep your watched history in sync, per Emby user.";

        public IEnumerable<PluginPageInfo> GetPages()
        {
            var ns = GetType().Namespace + ".Configuration.";
            return new[]
            {
                // the admin's page: users and their state, plus the app key
                new PluginPageInfo { Name = "wetrakr_admin", DisplayName = "WeTrakr", EmbeddedResourcePath = ns + "wetrakradmin.html", IsMainConfigPage = true },
                new PluginPageInfo { Name = "wetrakradminjs", EmbeddedResourcePath = ns + "wetrakradmin.js" },

                // each user's own page, reached from their profile menu
                new PluginPageInfo { Name = StaticName, EmbeddedResourcePath = ns + "wetrakr.html", EnableInUserMenu = true, FeatureId = StaticId },
                new PluginPageInfo { Name = "wetrakrjs", EmbeddedResourcePath = ns + "wetrakr.js" }
            };
        }

        public Stream GetThumbImage()
        {
            var type = GetType();
            return type.Assembly.GetManifestResourceStream(type.Namespace + ".thumb.png");
        }

        public ImageFormat ThumbImageFormat => ImageFormat.Png;
    }
}
