using System.Text.Json.Serialization;

namespace JellyfinLoader.Models
{
    internal class DependencyInfo
    {

        /// <summary>
        /// The manifest URL at which the plugin can be downloaded.
        /// </summary>
        [JsonPropertyName("manifest")]
        public required string Manifest { get; set; }

        /// <summary>
        /// The ID of the dependency plugin.
        /// </summary>
        [JsonPropertyName("id")]
        public required Guid ID { get; set; }

        /// <summary>
        /// The supported plugin versions.
        /// </summary>
        [JsonPropertyName("version")]
        public required List<Version> Versions { get; set; }
    }
}
