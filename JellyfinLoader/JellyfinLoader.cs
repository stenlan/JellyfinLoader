using CommandLine;
using Emby.Server.Implementations;
using Emby.Server.Implementations.AppBase;
using Emby.Server.Implementations.Library;
using Emby.Server.Implementations.Plugins;
using Emby.Server.Implementations.Serialization;
using HarmonyLib;
using Jellyfin.Extensions.Json;
using Jellyfin.Extensions.Json.Converters;
using Jellyfin.Server;
using Jellyfin.Server.Helpers;
using JellyfinLoader.Helpers;
using JellyfinLoader.Models;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Logging;
using Serilog.Extensions.Logging;
using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Xml.Serialization;

namespace JellyfinLoader
{
    public static class JellyfinLoader
    {
        private const string LoaderStubName = "JellyfinLoaderStub";
        private static bool _coldStart = true;
        private static Dictionary<string, Assembly> earlyLoadedAssemblies = new Dictionary<string, Assembly>();
        private static Dictionary<string, Assembly> earlyLoadedJLStubs = new Dictionary<string, Assembly>();
        private static List<object> earlyLoadPluginInstances = new();
        private static bool bootstrapRan = false;
        private static Harmony harmony;

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
            var loggerFactory = ((SerilogLoggerFactory)typeof(Program).GetField("_loggerFactory", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null));
            Utils.Logger = loggerFactory.CreateLogger("JellyfinLoader");
            Utils.Logger.LogInformation("In bootstrap.");

            var applicationPaths = StartupHelpers.CreateApplicationPaths(Parser.Default.ParseArguments<StartupOptions>(Environment.GetCommandLineArgs()).Value);
            Utils.PluginsPath = applicationPaths.PluginsPath;

            var pluginManager = new PluginManager(loggerFactory.CreateLogger<PluginManager>(), null, (ServerConfiguration)ConfigurationHelper.GetXmlConfiguration(typeof(ServerConfiguration), applicationPaths.SystemConfigurationFilePath, new MyXmlSerializer()), Utils.PluginsPath, Utils.AppVersion);
            typeof(PluginManager).GetField("_httpClientFactory", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(pluginManager, new JLHttpClientFactory());

            Utils.PluginManager = pluginManager;

            harmony = new Harmony("com.github.stenlan.jellyfinloader");
            // we ONLY apply the StartServer hook here, to ensure that any other "early" code is running in the same state regardless of
            // Bootstrap() being called by the disk-patched DLL or the trampoline
            harmony.Patch(AccessTools.DeclaredMethod(typeof(Program), "StartServer"), prefix: new HarmonyMethod(StartServerHook));
            // TODO: should we prevent createPluginInstance? or should it run as normal? or maybe just once?
            // harmony.Patch(AccessTools.Method(typeof(PluginManager), "CreatePluginInstance"), prefix: new HarmonyMethod(CreatePluginInstanceHook));
        }

        // All plugins are unloaded at this point. Harmony is available.
        private static bool StartServerHook(ref Task __result)
        {
            Utils.Logger.LogInformation("Server starting (ColdStart = {ColdStart})...", _coldStart);

            if (_coldStart)
            {
                harmony.Patch(AccessTools.Method(typeof(AssemblyLoadContext), "LoadFromAssemblyPath"), prefix: new HarmonyMethod(LoadFromAssemblyPathHook));
            }

            Utils.Logger.LogInformation("Resolving dependencies...");
            try
            {
                ResolveDependencies();
            }
            catch (Exception ex)
            {
                Utils.Logger.LogError(ex, "Encountered an error during dependency resolving. Shutting down...");
                typeof(Program).GetField("_restartOnShutdown", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, false);
                __result = Task.CompletedTask;
                return false;
            }

            foreach (var earlyLoadPluginInstance in earlyLoadPluginInstances)
            {
                earlyLoadPluginInstance.GetType().GetRuntimeMethod("OnServerStart", [typeof(bool)])?.Invoke(earlyLoadPluginInstance, [_coldStart]);
            }
            _coldStart = false;
            return true;
        }

        /// <summary>
        /// Called after Harmony has been loaded. Performs dependency resolving (and installation), and early loading of plugins.
        /// </summary>
        private static void ResolveDependencies()
        {
            var entryAssembly = Assembly.GetEntryAssembly()!;
            var alc = AssemblyLoadContext.GetLoadContext(entryAssembly)!;

            var myAssembly = Assembly.GetExecutingAssembly();
            var myDir = Path.GetDirectoryName(myAssembly.Location)!;
            var pluginsDir = Directory.GetParent(myDir)!.FullName;

            var dependencyResolver = new DependencyResolver(pluginsDir);

            dependencyResolver.ResolveAll().Wait();

            // make sure this happens after resolving dependencies, since it might have installed new plugins
            var pluginDirs = Directory.EnumerateDirectories(pluginsDir, "*.*", SearchOption.TopDirectoryOnly);

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
                    Utils.Logger.LogError("Failed to load plugin {Path}; encountered {Count} JellyfinLoader stubs, expected 1. Disabling plugin.", pluginDir, jlStubPaths.Count());
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
                        Utils.Logger.LogError(ex, "Failed to load assembly {Path}. Disabling plugin.", dllPath);
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
                        // TODO: maybe shut down server if this was in main load context since server is now in an invalid state.
                        Utils.Logger.LogError(ex, "Failed to load assembly {Path}. Unknown exception was thrown. Disabling plugin", assembly.Location);
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
                Utils.Logger.LogInformation("Returning early loaded assembly {assemblyPath}", assemblyPath);
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
