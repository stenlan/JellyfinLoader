using System.Text.Json.Serialization;

namespace JellyfinLoader
{
    internal class DependencyInfo
    {

        /// <summary>
        /// Gets or sets the manifest URL.
        /// </summary>
        [JsonPropertyName("manifest")]
        public required string Manifest { get; set; }

        /// <summary>
        /// Gets or sets the plugin ID.
        /// </summary>
        [JsonPropertyName("id")]
        public required string ID { get; set; }

        /// <summary>
        /// Gets or sets the plugin version.
        /// </summary>
        [JsonPropertyName("version")]
        public required string Version { get; set; }
    }
}
