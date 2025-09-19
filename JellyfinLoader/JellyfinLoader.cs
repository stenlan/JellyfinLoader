using Emby.Server.Implementations;
using Emby.Server.Implementations.Library;
using Emby.Server.Implementations.Plugins;
using HarmonyLib;
using Jellyfin.Extensions.Json;
using Jellyfin.Extensions.Json.Converters;
using Jellyfin.Server;
using JellyfinLoader.Models;
using JellyfinLoader.Utils;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Logging;
using Serilog.Extensions.Logging;
using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;

namespace JellyfinLoader
{
    public class JellyfinLoader
    {
        private const string LoaderStubName = "JellyfinLoaderStub";
        private static bool _coldStart = true;
        private static Dictionary<string, Assembly> earlyLoadedAssemblies = new Dictionary<string, Assembly>();
        private static Dictionary<string, Assembly> earlyLoadedJLStubs = new Dictionary<string, Assembly>();
        private static List<object> earlyLoadPluginInstances = new();
        private static bool bootstrapRan = false;
        private static Harmony harmony;

        internal static ILogger logger;

        [ModuleInitializer]
        internal static void ModuleInit()
        {
            // manually load harmony into main assembly load context
            var entryAssembly = Assembly.GetEntryAssembly()!;
            var myDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
            var alc = AssemblyLoadContext.GetLoadContext(entryAssembly)!;

            Assembly harmonyAssembly = alc.LoadFromAssemblyPath(Path.Combine(myDir, "0Harmony.dll"));
            harmonyAssembly.GetTypes();
        }

        /// <summary>
        /// Method called as early as possible by the disk-patched DLL, or once by the trampoline before soft restarting. Our DLL will be loaded into the main assembly load context.
        /// This method is NOT called again after a soft restart, so we are free to load any DLLs into the main assembly load context and blindly apply runtime patches.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Bootstrap()
        {
            // prevent stack overflow due to recursion through createlogger injection, and prevent double bootstrapping altogether
            if (bootstrapRan) return;
            bootstrapRan = true;
            logger = ((SerilogLoggerFactory)typeof(Program).GetField("_loggerFactory", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)).CreateLogger("JellyfinLoader");
            logger.LogInformation("In bootstrap.");

            harmony = new Harmony("com.github.stenlan.jellyfinloader");
            // we ONLY apply the StartServer hook here, to ensure that any other "early" code is running in the same state regardless of
            // Bootstrap() being called by the disk-patched DLL or the trampoline
            harmony.Patch(AccessTools.DeclaredMethod(typeof(Program), "StartServer"), prefix: new HarmonyMethod(StartServerHook));
            // TODO: should we prevent createPluginInstance? or should it run as normal? or maybe just once?
            // harmony.Patch(AccessTools.Method(typeof(PluginManager), "CreatePluginInstance"), prefix: new HarmonyMethod(CreatePluginInstanceHook));
        }

        // All plugins are unloaded at this point. Harmony is available.
        private static void StartServerHook()
        {
            logger.LogInformation("Server starting (ColdStart = {ColdStart})...", _coldStart);

            if (_coldStart)
            {
                harmony.Patch(AccessTools.Method(typeof(AssemblyLoadContext), "LoadFromAssemblyPath"), prefix: new HarmonyMethod(LoadFromAssemblyPathHook));
            }

            logger.LogInformation("Resolving dependencies...");
            ResolveDependencies().Wait();

            foreach (var earlyLoadPluginInstance in earlyLoadPluginInstances)
            {
                earlyLoadPluginInstance.GetType().GetRuntimeMethod("OnServerStart", [typeof(bool)])?.Invoke(earlyLoadPluginInstance, [_coldStart]);
            }
            _coldStart = false;
        }

        /// <summary>
        /// Called after Harmony has been loaded. Performs dependency resolving (and installation), and early loading. Also performs
        /// required Harmony patches.
        /// </summary>
        private static async Task ResolveDependencies()
        {
            var entryAssembly = Assembly.GetEntryAssembly()!;
            var alc = AssemblyLoadContext.GetLoadContext(entryAssembly)!;

            var myAssembly = Assembly.GetExecutingAssembly();
            var myDir = Path.GetDirectoryName(myAssembly.Location)!;
            var pluginsDir = Directory.GetParent(myDir)!.FullName;

            var pluginDirs = Directory.EnumerateDirectories(pluginsDir, "*.*", SearchOption.TopDirectoryOnly);
            HashSet<LocalDependencyInfo> dependencies = [];
            Dictionary<Guid, HashSet<InstalledPluginInfo>> installedPlugins = [];

            // TODO: more thoroughly handle disabled states, conflicting versions, missing meta.jsons, etc

            // first, discover all installed plugins and dependencies
            foreach (var pluginDir in pluginDirs)
            {
                var pluginManifest = PluginHelper.ReadPluginManifest(pluginDir);
                if (pluginManifest == null) continue;

                var validVersion = Version.TryParse(pluginManifest.Version, out var pluginVersion);

                if (!validVersion)
                {
                    logger.LogWarning("Plugin at {pluginDir} has an invalid version in its meta.json: {ver}. It is unsupported by JellyLoader both as a dependency and as a dependent.", pluginDir, pluginManifest.Version);
                    continue;
                }

                var hasEntry = installedPlugins.TryGetValue(pluginManifest.Id, out var pluginInfos);
                if (!hasEntry)
                {
                    installedPlugins[pluginManifest.Id] = pluginInfos = [];
                }

                pluginInfos!.Add(new InstalledPluginInfo(pluginManifest.Id, pluginVersion!, pluginManifest.Status, pluginManifest.Name, pluginDir));

                var loaderManifest = PluginHelper.ReadLoaderManifest(pluginDir);
                if (loaderManifest == null) continue;

                // don't resolve or install dependencies for inactive plugins
                if (pluginManifest.Status != PluginStatus.Active) continue;

                if (loaderManifest.Dependencies.Any(d => d.Versions.Count > 1))
                {
                    logger.LogCritical("Currently, only a single version per dependency is supported. Shutting down...");
                    // TODO: graceful exit
                    Environment.Exit(1);
                    return;
                }

                loaderManifest.Dependencies.ForEach(d => dependencies.Add(new LocalDependencyInfo(d.Manifest, d.ID, d.Versions, pluginManifest.Id)));
            }

            foreach (var pluginInfos in installedPlugins.Values)
            {
                var activeVersions = pluginInfos.Where(pluginInfo => pluginInfo.Status == PluginStatus.Active);

                if (activeVersions.Count() > 1)
                {
                    var aVersion = activeVersions.First();
                    var joinedPaths = new StringBuilder();
                    foreach (var activeVersion in activeVersions)
                    {
                        joinedPaths.AppendLine(activeVersion.Path);
                    }
                    logger.LogWarning("Found multiple enabled versions of plugin with ID {pluginID} and possible name {name} at paths:\n{paths}", aVersion.ID, aVersion.Name, joinedPaths.ToString());
                }
            }

            // find out which dependencies are missing
            dependencies.RemoveWhere(dependency =>
            {
                var isPluginInstalled = installedPlugins.TryGetValue(dependency.ID, out var pluginVersions);
                if (!isPluginInstalled) return false;

                // TODO: check if different version _is_ installed and maybe required by other plugin or not, possibly update
                var matchingVersions = pluginVersions!.Where(version => dependency.Versions.Contains(version.Version));

                // no matching version installed
                if (!matchingVersions.Any()) return false;

                // matching version is installed but not active
                if (!matchingVersions.Any(version => version.Status == PluginStatus.Active))
                {
                    logger.LogWarning("Dependency {depName} is required by {dependentName} and it is installed, but disabled!", matchingVersions.First().Name, installedPlugins[dependency.Dependent]!.First().Name);
                }

                return true;
            });

            var depResolver = new DependencyResolver();

            // attempt to install missing dependencies
            while (dependencies.Count > 0)
            {
                // TODO: smart dependency resolving:
                // Dependencies might be required by multiple different plugins, and thus there might be several entries for the same dependency, with possibly different required versions.
                // If they don't overlap, we are done and we cannot resolve the dependencies.
                // If they do overlap, there might be multiple candidate versions for a dependency, we need to find out which one to install.
                // We might be tempted to just pick a random one that satisfies all current dependencies, but it itself might then have dependencies that might
                // conflict with others, etc. So we first need to build a full dependency tree before persisting any installs.

                // var packages = PluginHelper.GetPackages()
            }

            foreach (var pluginDir in pluginDirs)
            {
                var loaderManifest = PluginHelper.ReadLoaderManifest(pluginDir);
                if (loaderManifest == null) continue;

                if (loaderManifest.LoadControl != "EARLY") continue;

                // TODO: handle non-early load plugins, and handle dependencies

                // now we take control of the plugin
                var localPlugin = PluginHelper.ReadLocalPlugin(pluginDir);

                var dllFilePaths = PluginHelper.GetLoaderPluginDLLs(localPlugin, loaderManifest);
                var jlStubPaths = dllFilePaths.Where(path => Path.GetFileName(path) == LoaderStubName + ".dll");

                if (jlStubPaths.Count() != 1)
                {
                    logger.LogError("Failed to load plugin {Path}; encountered {Count} JellyfinLoader stubs, expected 1. Disabling plugin.", pluginDir, jlStubPaths.Count());
                    localPlugin.Manifest.Status = PluginStatus.Disabled;
                    PluginHelper.SaveManifests(pluginDir, localPlugin.Manifest, loaderManifest);
                    continue;
                }

                var jlStubPath = jlStubPaths.First()!;

                var assemblies = new List<Assembly>(dllFilePaths.Count);
                var loadedAll = true;

                foreach (var dllPath in dllFilePaths)
                {
                    if (dllPath == jlStubPath) // this is the JL Stub, skip adding it here as it is added from its referencing assemblies
                    {
                        continue;
                    }

                    try
                    {
                        var assembly = alc.LoadFromAssemblyPath(dllPath);

                        foreach (var referencedAssembly in assembly.GetReferencedAssemblies())
                        {
                            if (referencedAssembly.Name == LoaderStubName) // references a JL stub
                            {
                                var stubLoaded = earlyLoadedJLStubs.TryGetValue(referencedAssembly.ToString(), out var stubAssembly);
                                if (stubLoaded)
                                {
                                    earlyLoadedAssemblies.Add(jlStubPath, stubAssembly!);
                                } else
                                {
                                    var newStubAssembly = alc.LoadFromAssemblyPath(jlStubPath);
                                    if (newStubAssembly.GetName().ToString() != referencedAssembly.ToString())
                                    {
                                        throw new InvalidOperationException($"Failed to load plugin {pluginDir}; The plugin was built against a different JellyfinLoader stub than the binary that was shipped with it. It was built against \"{referencedAssembly}\", but the included binary is \"{newStubAssembly.GetName()}\".");
                                    }
                                    assemblies.Add(newStubAssembly);
                                    earlyLoadedJLStubs.Add(referencedAssembly.ToString(), newStubAssembly);
                                }
                            }
                        }

                        assemblies.Add(assembly);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to load assembly {Path}. Disabling plugin.", dllPath);
                        localPlugin.Manifest.Status = PluginStatus.Disabled;
                        PluginHelper.SaveManifests(pluginDir, localPlugin.Manifest, loaderManifest);
                        loadedAll = false;
                        break;
                    }
                }

                if (!loadedAll) continue;

                var earlyLoadPluginHandlerTypes = new List<Type>();

                foreach (var assembly in assemblies)
                {
                    try
                    {
                        // Load all required types to verify that the plugin will load
                        var assemblyTypes = assembly.GetTypes();
                        earlyLoadPluginHandlerTypes.AddRange(assemblyTypes.Where(type => type.GetInterface("JellyfinLoader.IEarlyLoadPlugin") != null));
                        earlyLoadedAssemblies.Add(assembly.Location, assembly);
                    }
                    catch (Exception ex)
                    {
                        // TODO: maybe shut down server since it is now in an invalid state.
                        logger.LogError(ex, "Failed to load assembly {Path}. Unknown exception was thrown. Disabling plugin", assembly.Location);
                        localPlugin.Manifest.Status = PluginStatus.Disabled;
                        PluginHelper.SaveManifests(pluginDir, localPlugin.Manifest, loaderManifest);
                        loadedAll = false;
                        break;
                    }
                }

                if (!loadedAll) continue;

                foreach (var earlyLoadPluginHandlerType in earlyLoadPluginHandlerTypes)
                {
                    earlyLoadPluginInstances.Add(Activator.CreateInstance(earlyLoadPluginHandlerType));
                }

                localPlugin.Manifest.Status = PluginStatus.Active;
                PluginHelper.SaveManifests(pluginDir, localPlugin.Manifest, loaderManifest);
            }
        }

        private static bool LoadFromAssemblyPathHook(AssemblyLoadContext __instance, string assemblyPath, ref Assembly __result)
        {
            if (__instance is not PluginLoadContext instance) return true;

            var isEarlyLoaded = earlyLoadedAssemblies.TryGetValue(assemblyPath, out var earlyAssembly);

            if (isEarlyLoaded)
            {
                logger.LogInformation("Returning early loaded assembly {assemblyPath}", assemblyPath);
                __result = earlyAssembly;
            }

            return !isEarlyLoaded; // only continue if not early loaded
        }

        //private static bool CreatePluginInstanceHook(Type type, ref IPlugin? __result)
        //{
        //    var assemblyPath = type.Assembly.Location;
        //    var isEarlyLoaded = earlyLoadedAssemblies.TryGetValue(assemblyPath, out var earlyAssembly);

        //    if (isEarlyLoaded)
        //    {
        //        _logger.LogInformation("Returning early loaded assembly {assemblyPath}", assemblyPath);
        //        __result = null;
        //    }

        //    return !isEarlyLoaded; // only continue if not early loaded
        //    if (__instance is not PluginLoadContext instance) return true;

        //    var isEarlyLoaded = earlyLoadedAssemblies.TryGetValue(assemblyPath, out var earlyAssembly);

        //    if (isEarlyLoaded)
        //    {
        //        _logger.LogInformation("Returning early loaded assembly {assemblyPath}", assemblyPath);
        //        __result = earlyAssembly;
        //    }

        //    return !isEarlyLoaded; // only continue if not early loaded
        //}
    }
}
