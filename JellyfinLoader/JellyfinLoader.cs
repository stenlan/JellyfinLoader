using Emby.Server.Implementations;
using Emby.Server.Implementations.Library;
using Emby.Server.Implementations.Plugins;
using HarmonyLib;
using Jellyfin.Extensions.Json;
using Jellyfin.Extensions.Json.Converters;
using Jellyfin.Server;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Logging;
using Serilog.Extensions.Logging;
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

        internal static ILogger logger;

        /// <summary>
        /// Method called as early as possible by the disk-patched DLL, or once by the trampoline before soft restarting. Our DLL will be loaded into the main assembly load context,
        /// but nothing else (dnlib, harmony), so we should take care of that ourselves. This method is NOT called again after a soft restart,
        /// so we are free to load any DLLs into the main assembly load context and blindly apply runtime patches.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Bootstrap(IServerApplicationPaths appPaths)
        {
            logger = ((SerilogLoggerFactory)typeof(Program).GetField("_loggerFactory", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)).CreateLogger("JellyfinLoader");
            logger.LogInformation("In bootstrap.");

            // manually load harmony into main assembly load context
            var entryAssembly = Assembly.GetEntryAssembly()!;
            var myDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
            var alc = AssemblyLoadContext.GetLoadContext(entryAssembly)!;

            Assembly harmonyAssembly = alc.LoadFromAssemblyPath(Path.Combine(myDir, "0Harmony.dll"));
            harmonyAssembly.GetTypes();

            // branch to separate method since types are loaded on method entry, so using Harmony
            // in the current method would cause an error
            BootstrapStep2();
        }

        /// <summary>
        /// Called after Harmony has been loaded. Performs dependency resolving (and installation), and early loading. Also performs
        /// required Harmony patches.
        /// </summary>
        private static void BootstrapStep2()
        {
            var entryAssembly = Assembly.GetEntryAssembly()!;
            var alc = AssemblyLoadContext.GetLoadContext(entryAssembly)!;

            var myAssembly = Assembly.GetExecutingAssembly();
            var myDir = Path.GetDirectoryName(myAssembly.Location)!;
            var pluginsDir = Directory.GetParent(myDir)!.FullName;

            var pluginDirs = Directory.EnumerateDirectories(pluginsDir, "*.*", SearchOption.TopDirectoryOnly);

            // first, enumerate all plugin dirs and install any non-installed dependencies
            foreach (var pluginDir in pluginDirs)
            {

            }


            foreach (var pluginDir in pluginDirs)
            {
                var loaderManifest = PluginHelper.ReadLoaderManifest(pluginDir);
                if (loaderManifest == null) continue;

                if (loaderManifest.LoadControl != "EARLY") continue;

                // TODO: handle non-early load plugins, and handle dependencies

                // now we take control of the plugin
                var localPlugin = PluginHelper.ReadLocalPlugin(pluginDir);

                if (!loaderManifest.Enabled) // disable the plugin in the jellyfin manifest too
                {
                    localPlugin.Manifest.Status = PluginStatus.Disabled;
                    PluginHelper.SavePluginManifest(localPlugin.Manifest, pluginDir);
                    continue;
                }

                var dllFilePaths = PluginHelper.GetLoaderPluginDLLs(localPlugin, loaderManifest);
                var jlStubPaths = dllFilePaths.Where(path => Path.GetFileName(path) == LoaderStubName + ".dll");

                if (jlStubPaths.Count() != 1)
                {
                    logger.LogError("Failed to load plugin {Path}; encountered {Count} JellyfinLoader stubs, expected 1. Disabling plugin.", pluginDir, jlStubPaths.Count());
                    loaderManifest.Enabled = false;
                    PluginHelper.SaveLoaderManifest(loaderManifest, pluginDir);
                    localPlugin.Manifest.Status = PluginStatus.Disabled;
                    PluginHelper.SavePluginManifest(localPlugin.Manifest, pluginDir);
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
                        loaderManifest.Enabled = false;
                        PluginHelper.SaveLoaderManifest(loaderManifest, pluginDir);

                        localPlugin.Manifest.Status = PluginStatus.Disabled;
                        PluginHelper.SavePluginManifest(localPlugin.Manifest, pluginDir);
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
                        earlyLoadPluginHandlerTypes.AddRange(assemblyTypes.Where(type => type.FullName == "JellyfinLoader.IEarlyLoadPlugin"));
                        earlyLoadedAssemblies.Add(assembly.Location, assembly);
                    }
                    catch (Exception ex)
                    {
                        // TODO: maybe shut down server since it is now in an invalid state.
                        logger.LogError(ex, "Failed to load assembly {Path}. Unknown exception was thrown. Disabling plugin", assembly.Location);
                        loaderManifest.Enabled = false;
                        PluginHelper.SaveLoaderManifest(loaderManifest, pluginDir);

                        localPlugin.Manifest.Status = PluginStatus.Disabled;
                        PluginHelper.SavePluginManifest(localPlugin.Manifest, pluginDir);
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
                PluginHelper.SavePluginManifest(localPlugin.Manifest, pluginDir);
            }

            var harmony = new Harmony("com.github.stenlan.jellyfinloader");
            harmony.Patch(AccessTools.Method(typeof(AssemblyLoadContext), "LoadFromAssemblyPath"), prefix: new HarmonyMethod(LoadFromAssemblyPathHook));
            // TODO: should we prevent createPluginInstance? or should it run as normal? or maybe just once?
            // harmony.Patch(AccessTools.Method(typeof(PluginManager), "CreatePluginInstance"), prefix: new HarmonyMethod(CreatePluginInstanceHook));
            harmony.Patch(AccessTools.DeclaredMethod(typeof(Program), "StartServer"), prefix: new HarmonyMethod(StartServerHook));
        }

        

        private static void StartServerHook()
        {
            logger.LogInformation("Server starting (ColdStart = {ColdStart})...", _coldStart);
            foreach (var earlyLoadPluginInstance in earlyLoadPluginInstances)
            {
                earlyLoadPluginInstance.GetType().GetRuntimeMethod("OnServerStart", [typeof(bool)])?.Invoke(earlyLoadPluginInstance, [_coldStart]);
            }
            _coldStart = false;
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
