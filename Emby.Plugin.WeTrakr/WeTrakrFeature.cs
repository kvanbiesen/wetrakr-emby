using System.Collections.Generic;
using Emby.Features;

namespace Emby.Plugin.WeTrakr
{
    /// <summary>
    /// Registers WeTrakr as a per-user feature: it shows up in each user's permissions, so an
    /// admin can switch it off for someone, and the plugin then ignores that user everywhere.
    /// </summary>
    public class WeTrakrFeature : IFeatureFactory
    {
        public List<FeatureInfo> GetFeatureInfos(string language)
        {
            return new List<FeatureInfo>
            {
                new FeatureInfo { Id = Plugin.StaticId, Name = Plugin.StaticName, FeatureType = FeatureType.User }
            };
        }
    }
}
