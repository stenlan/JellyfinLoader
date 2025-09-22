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
        /// It is an error for a plugin with its loadContext set to "Main" to depend on a plugin that has its loadContext set to "Plugin" (or a non-JellyfinLoader plugin).
        /// 
        /// In case a plugin is loaded into the main load context, it is not unloaded between server soft restarts, and there are some other limitations and pitfalls.
        /// TODO: document limitations and pitfalls
        /// </summary>
        [JsonPropertyName("loadContext")]
        public string LoadContext { get; set; } = "Plugin";

        /// <summary>
        /// The moment at which you want the plugin's assemblies to be loaded, either "Default" or "Early".
        /// 
        /// Plugins are never loaded earlier than their dependencies, so if any of your dependencies have a Default load timing, then your plugin
        /// will also have a Default load timing, even if you specify "Early" here. A "Default" load timing will always be respected, even if your
        /// dependencies have an "Early" load timing. JellyfinLoader will warn you when this value is not respected.
        /// 
        /// Plugins that have an Early load timing can use the JellyfinLoader.IEarlyLoadPlugin interface.
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
