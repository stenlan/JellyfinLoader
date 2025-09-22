using Emby.Server.Implementations.Plugins;
using JellyfinLoader.Models;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Threading.Tasks;

namespace JellyfinLoader.Helpers
{
    internal class AssemblyLoader(Utils utils, PluginIOHelper pluginIOHelper, DependencyResolver resolver)
    {
        private readonly AssemblyLoadContext mainALC = AssemblyLoadContext.GetLoadContext(Assembly.GetEntryAssembly()!)!;
        private readonly Dictionary<string, JLAssemblyData> jlManagedAssemblies = [];

        private readonly Dictionary<string, Assembly> mainLoadedJLStubs = [];
        private readonly Dictionary<int, Dictionary<string, Assembly>> earlyLoadedJLStubs = [];

        /// <summary>
        /// Discover all assemblies
        /// </summary>
        public void OnServerStart()
        {
            jlManagedAssemblies.Clear();
            earlyLoadedJLStubs.Clear();
        }

        public void LoadEarlyPlugins(bool coldStart)
        {
            List<object> earlyLoadPluginInstances = [];

            foreach (var (depPoolID, depPool) in resolver.dependencyPools)
            {
                // create a pluginLoadContext for every dependency pool
                var loadContext = new PluginLoadContext(string.Empty);
                var mainLoadContextAllowed = true;
                var earlyLoadAllowed = true;
                var canLoad = true;

                foreach (var pluginID in depPool)
                {
                    // TODO: this might be undefined if we are disabling any?
                    var plugin = resolver.GetPluginInfo(pluginID) ?? throw new InvalidOperationException("Inactive plugin in dependency pool.");

                    // a non-JL aware plugin will never be early loaded nor loaded into main load context, no further checks needed
                    if (plugin.LoaderManifest == null)
                    {
                        ConfigureAssemblies(plugin.Path, plugin.Manifest, loadContext, depPoolID);
                        earlyLoadAllowed = false;
                        continue;
                    }

                    // treat the same as non-JL aware, but warn user
                    if (plugin.Manifest.Assemblies.Count != 1)
                    {
                        ConfigureAssemblies(plugin.Path, plugin.Manifest, loadContext, depPoolID);
                        utils.Logger.LogWarning("Despite containing a loader.json file, the plugin at {pluginPath} will not be treated as a JellyfinLoader plugin because its meta.json contains more than 1 DLL in its \"assemblies\" entry. It should contain just the JellyfinLoaderStub assembly, its loader.json's \"assemblies\" entry can optionally contain an actual assembly whitelist (or be left blank).", plugin.Path);
                        earlyLoadAllowed = false;
                        continue;
                    }

                    var wantsEarlyLoad = plugin.LoaderManifest.LoadTiming == "Early";
                    if (wantsEarlyLoad && !earlyLoadAllowed)
                    {
                        utils.Logger.LogWarning("Despite specifying a loadTiming of \"Early\" in its loader.json file, the plugin at {pluginPath} will not be early loaded, because it depends on at least 1 non-early loading plugin.", plugin.Path);
                    }

                    // whenever we encounter a non-early loading plugin in the chain, this plugin and every one after it in this pool will load with default load timing
                    earlyLoadAllowed &= wantsEarlyLoad;

                    var mainLoadContext = plugin.LoaderManifest.LoadContext == "Main";
                    if (mainLoadContext && !mainLoadContextAllowed)
                    {
                        throw new InvalidOperationException($"Plugin at {plugin.Path} specifies a \"Main\" load context, but it depends on at least 1 plugin that does not.");
                    }

                    // don't allow main context loads after non-main context loads
                    mainLoadContextAllowed &= mainLoadContext;

                    ConfigureAssemblies(plugin.Path, plugin.Manifest, plugin.LoaderManifest, mainLoadContext ? mainALC : loadContext, mainLoadContext ? null : depPoolID);

                    if (!(wantsEarlyLoad && earlyLoadAllowed)) continue;

                    if (!canLoad)
                    {
                        // TODO: skip load instead, signal to pluginManager somehow
                        utils.Logger.LogWarning("Marking {pluginName} as malfunctioned because some of its dependencies failed to load.", plugin.Manifest.Name);
                        resolver.ChangePluginState(plugin, PluginStatus.Malfunctioned);
                        continue;
                    }

                    var loadSuccess = EarlyLoadPlugin(plugin.Path, plugin.Manifest, plugin.LoaderManifest, out var elPluginInstances);

                    if (loadSuccess) continue;

                    canLoad = false;
                    resolver.ChangePluginState(plugin, PluginStatus.Malfunctioned);

                    if (mainLoadContext)
                    {
                        throw new InvalidOperationException($"Plugin {plugin.Manifest.Name} at {plugin.Path} has its loadContext set to \"main\" but failed during early load!");
                    }
                }
            }

            foreach (var earlyLoadPluginInstance in earlyLoadPluginInstances)
            {
                earlyLoadPluginInstance.GetType().GetRuntimeMethod("OnServerStart", [typeof(bool)])?.Invoke(earlyLoadPluginInstance, [coldStart]);
            }
        }

        /// <summary>
        /// Configure assemblies for JellyfinLoader-aware plugin
        /// </summary>
        private void ConfigureAssemblies(string pluginPath, PluginManifest pluginManifest, LoaderPluginManifest loaderManifest, AssemblyLoadContext loadContext, int? depPoolID)
        {
            var (stubPath, dllFilePaths) = pluginIOHelper.GetJLAwarePluginDLLs(pluginPath, pluginManifest, loaderManifest);
            jlManagedAssemblies[stubPath] = new JLAssemblyData(loadContext, depPoolID);
            foreach (var dllPath in dllFilePaths)
            {
                jlManagedAssemblies[dllPath] = new JLAssemblyData(loadContext, depPoolID);
            }
        }

        /// <summary>
        /// Configure assemblies for JellyfinLoader-unaware plugin
        /// </summary>
        private void ConfigureAssemblies(string pluginPath, PluginManifest pluginManifest, AssemblyLoadContext loadContext, int depPoolID)
        {
            var dllFilePaths = pluginIOHelper.GetPluginDLLs(pluginPath, pluginManifest);
            foreach (var dllPath in dllFilePaths) {
                jlManagedAssemblies[dllPath] = new JLAssemblyData(loadContext, depPoolID);
            }
        }

        private bool EarlyLoadPlugin(string pluginPath, PluginManifest pluginManifest, LoaderPluginManifest loaderManifest, out List<object> earlyLoadPluginInstances)
        {
            var (stubPath, dllFilePaths) = pluginIOHelper.GetJLAwarePluginDLLs(pluginPath, pluginManifest, loaderManifest);
            var stubName = Path.GetFileNameWithoutExtension(stubPath);
            earlyLoadPluginInstances = [];

            var assemblies = new List<Assembly>();

            foreach (var dllPath in dllFilePaths)
            {
                try
                {
                    var assemblyData = jlManagedAssemblies[dllPath];
                    var assembly = assemblyData.AssemblyLoadContext.LoadFromAssemblyPath(dllPath);
                    assemblies.Add(assembly);

                    var stubReferences = assembly.GetReferencedAssemblies().Where(assembly => assembly.Name == stubName);

                    if (!stubReferences.Any()) continue;
                    if (stubReferences.Count() > 1)
                    {
                        throw new InvalidOperationException($"Failed to load plugin {pluginManifest.Name}; The plugin was built against more than 1 version of the JellyfinLoaderStub assembly at the same time!");
                    }

                    var stubReference = stubReferences.First();

                    var loadedJLStubs = assemblyData.DepPoolId.HasValue ? earlyLoadedJLStubs.GetOrAdd(assemblyData.DepPoolId.Value, []) : mainLoadedJLStubs;
                    var stubLoaded = loadedJLStubs.TryGetValue(stubReference.FullName, out var stubAssembly);
                    if (stubLoaded) continue;

                    var newStubAssembly = jlManagedAssemblies[stubPath].AssemblyLoadContext.LoadFromAssemblyPath(stubPath);
                    if (newStubAssembly.GetName().FullName != stubReference.FullName)
                    {
                        throw new InvalidOperationException($"Failed to load plugin {pluginManifest.Name}; The plugin was built against a different JellyfinLoader stub than the binary that was shipped with it. It was built against \"{stubReference.FullName}\", but the included binary is \"{newStubAssembly.GetName().FullName}\".");
                    }
                    assemblies.Add(newStubAssembly);
                    loadedJLStubs.Add(stubReference.FullName, newStubAssembly);
                }
                catch (Exception ex)
                {
                    utils.Logger.LogError(ex, "Failed to load assembly {Path}.", dllPath);
                    return false;
                }
            }

            var earlyLoadPluginHandlerTypes = new List<Type>();

            foreach (var assembly in assemblies)
            {
                try
                {
                    // Load all required types to verify that the plugin will load
                    var assemblyTypes = assembly.GetTypes();
                    earlyLoadPluginHandlerTypes.AddRange(assemblyTypes.Where(type => type.GetInterface("JellyfinLoader.IEarlyLoadPlugin") != null));
                }
                catch (Exception ex)
                {
                    utils.Logger.LogError(ex, "Failed to load assembly {Path}. Unknown exception was thrown. ", assembly.Location);
                    return false;
                }
            }

            foreach (var earlyLoadPluginHandlerType in earlyLoadPluginHandlerTypes)
            {
                earlyLoadPluginInstances.Add(Activator.CreateInstance(earlyLoadPluginHandlerType)!);
            }

            pluginIOHelper.SaveLoaderManifest(loaderManifest, pluginPath);
            return true;
        }

        /// <summary>
        /// PluginLoadContext#LoadFromAssemblyPath hook
        /// </summary>
        internal bool LoadFromAssemblyPath(string assemblyPath, ref Assembly result)
        {
            var isJLManaged = jlManagedAssemblies.TryGetValue(assemblyPath, out var assemblyData);
            if (!isJLManaged || assemblyData == null || assemblyData.LoadAllowed == true) return true;

            // managed and already loaded
            if (assemblyData.Assembly is not null)
            {
                utils.Logger.LogInformation("Returning assembly {assemblyPath} to PluginManager.", assemblyPath);
                result = assemblyData.Assembly;
                return false;
            }

            // managed, but not yet loaded.
            assemblyData.LoadAllowed = true;
            result = assemblyData.AssemblyLoadContext.LoadFromAssemblyPath(assemblyPath);
            assemblyData.LoadAllowed = false;

            return false;
        }
    }
}
