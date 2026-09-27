using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Emby.Plugin.WeTrakr.Tests
{
    public class PackagingTests
    {
        private static readonly Assembly Plugin = typeof(Emby.Plugin.WeTrakr.Plugin).Assembly;

        private static string Read(string name)
        {
            using (var reader = new StreamReader(Plugin.GetManifestResourceStream(name)))
            {
                return reader.ReadToEnd();
            }
        }

        [Theory]
        [InlineData("wetrakr.html")]
        [InlineData("wetrakr.js")]
        [InlineData("wetrakradmin.html")]
        [InlineData("wetrakradmin.js")]
        public void Every_page_resource_the_plugin_serves_is_embedded_and_not_a_stub(string page)
        {
            var name = "Emby.Plugin.WeTrakr.Configuration." + page;
            Assert.Contains(name, Plugin.GetManifestResourceNames());
            Assert.True(Read(name).Length > 500, page + " looks like a placeholder");
        }

        [Fact]
        public void The_thumbnail_is_embedded()
        {
            Assert.Contains("Emby.Plugin.WeTrakr.thumb.png", Plugin.GetManifestResourceNames());
        }

        [Fact]
        public void Each_page_names_the_controller_script_the_plugin_registers()
        {
            Assert.Contains("data-controller=\"__plugin/wetrakrjs\"", Read("Emby.Plugin.WeTrakr.Configuration.wetrakr.html"));
            Assert.Contains("data-controller=\"__plugin/wetrakradminjs\"", Read("Emby.Plugin.WeTrakr.Configuration.wetrakradmin.html"));
        }

        [Fact]
        public void The_admin_page_opens_the_page_the_plugin_registers_for_users()
        {
            Assert.Contains("configurationpage?name=" + Emby.Plugin.WeTrakr.Plugin.StaticName + "&userId=", Read("Emby.Plugin.WeTrakr.Configuration.wetrakradmin.js"));
        }

        [Fact]
        public void The_pages_use_the_same_settings_key_and_routes_the_server_registers()
        {
            var js = Read("Emby.Plugin.WeTrakr.Configuration.wetrakr.js");
            Assert.Contains("\"" + Emby.Plugin.WeTrakr.Configuration.ConfigurationFactory.OptionsKey + "\"", js);
            foreach (var route in new[] { "WeTrakr/oauth/", "/start", "/poll", "/cancel", "/logout", "WeTrakr/account/", "WeTrakr/libraries/", "WeTrakr/sync/" })
            {
                Assert.Contains(route, js);
            }
        }

        [Fact]
        public void No_secret_is_embedded_in_a_page()
        {
            foreach (var name in Plugin.GetManifestResourceNames().Where(n => n.EndsWith(".js") || n.EndsWith(".html")))
            {
                Assert.DoesNotContain("client_secret", Read(name).Replace("Never paste a client secret here", ""));
            }
        }
    }
}
