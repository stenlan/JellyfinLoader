using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using System.Text.Json.Serialization;

namespace JellyfinLoader
{
    internal class LoaderPluginManifest
    {
        /// <summary>
        /// Either DEFAULT or EARLY
        /// </summary>
        [JsonPropertyName("loadControl")]
        public string LoadControl { get; set; } = "DEFAULT";

        /// <summary>
        /// Whether or not JellyfinLoader considers this plugin to be enabled. Overrides internal Jellyfin status.
        /// </summary>
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;
    }
}
