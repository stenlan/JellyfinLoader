using System.Text.Json.Serialization;

namespace JellyfinLoader.Models
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
        public required Guid ID { get; set; }

        /// <summary>
        /// Gets or sets the supported plugin versions.
        /// </summary>
        [JsonPropertyName("version")]
        public required List<Version> Versions { get; set; }
    }
}
