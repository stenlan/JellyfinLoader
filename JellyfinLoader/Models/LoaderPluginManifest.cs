using System.Text.Json.Serialization;
using System.Runtime.Loader;

namespace JellyfinLoader.Models
{
    internal class LoaderPluginManifest
    {
        /// <summary>
        /// The <see cref="AssemblyLoadContext"/> of the plugin, either "Plugin" or "Main". Note that you almost always want to use Plugin, even as a library plugin/when you expect
        /// other plugins to depend on your plugin. The dependency resolver will take care of load order.
        /// 
        /// If any dependencies are specified, this value might not be respected.
        /// 
        /// In case 'Main' is used, the plugin is not unloaded between server soft restarts, and there are some other limitations and pitfalls
        /// TODO: document limitations and pitfalls
        /// </summary>
        [JsonPropertyName("loadContext")]
        public string LoadContext { get; set; } = "Plugin";

        /// <summary>
        /// The moment at which you want the plugin's assemblies to be loaded, either "Default" or "Early".
        /// 
        /// Plugins are never loaded earlier than their dependencies, so if any of your dependencies have a Default load timing, then your plugin
        /// will also have a Default load timing, even if you specify "Early" here.
        /// </summary>
        [JsonPropertyName("loadTiming")]
        public string LoadTiming { get; set; } = "Default";

        /// <summary>
        /// The collection of assemblies that should be loaded.
        /// Paths are considered relative to the plugin's folder.
        /// </summary>
        [JsonPropertyName("assemblies")]
        public List<string> Assemblies { get; set; } = [];

        /// <summary>
        /// The collection of dependencies.
        /// </summary>
        [JsonPropertyName("dependencies")]
        public List<DependencyInfo> Dependencies { get; set; } = [];
    }
}
