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

        /// <summary>
        /// Gets or sets the collection of assemblies that should be loaded.
        /// Paths are considered relative to the plugin folder.
        /// </summary>
        [JsonPropertyName("assemblies")]
        public List<string> Assemblies { get; set; } = [];
    }
}
