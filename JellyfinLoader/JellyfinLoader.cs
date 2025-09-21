using HarmonyLib;
using Jellyfin.Server;
using JellyfinLoader.Helpers;
using JellyfinLoader.Hooks;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Logging;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace JellyfinLoader
{
    public class JellyfinLoader
    {
        private const string LoaderStubName = "JellyfinLoaderStub";
        
        private static JellyfinLoader? _instance;
        public static JellyfinLoader Instance { get => _instance ?? throw new NullReferenceException("Attempted to access JellyfinLoader instance before its initialization."); }
        private static bool _bootstrapRan = false;
        
        private bool _coldStart = true;
        private readonly Dictionary<string, Assembly> earlyLoadedAssemblies = new Dictionary<string, Assembly>();
        private readonly Dictionary<string, Assembly> earlyLoadedJLStubs = new Dictionary<string, Assembly>();
        private readonly List<object> earlyLoadPluginInstances = new();

        private readonly Utils utils;
        private readonly Harmony harmony;
        private readonly PluginHelper pluginHelper;
        private readonly DependencyResolver resolver;

        private JellyfinLoader()
        {
            utils = new Utils();
            utils.Logger.LogInformation("In bootstrap.");

            harmony = new Harmony("com.github.stenlan.jellyfinloader");
            // we ONLY apply the StartServer hook here, to ensure that any other "early" code is running in the same state regardless of
            // Bootstrap() being called by the disk-patched DLL or the trampoline
            harmony.PatchCategory(Assembly.GetExecutingAssembly(), nameof(StartServerHook));

            pluginHelper = new PluginHelper(utils);
            resolver = new DependencyResolver(utils, pluginHelper);
            
            // TODO: should we prevent createPluginInstance? or should it run as normal? or maybe just once?
            // harmony.Patch(AccessTools.Method(typeof(PluginManager), "CreatePluginInstance"), prefix: new HarmonyMethod(CreatePluginInstanceHook));
        }

        /// <summary>
        /// StartServer method that is called by the StartServer hook.
        /// </summary>
        /// <returns>A boolean indicating whether or not the startup should continue (true), or the server should shut down (false).</returns>
        internal bool StartServer()
        {
            utils.Logger.LogInformation("Server starting (ColdStart = {ColdStart})...", _coldStart);

            if (_coldStart)
            {
                harmony.PatchCategory(Assembly.GetExecutingAssembly(), nameof(LoadFromAssemblyPathHook));
            }

            utils.Logger.LogInformation("Resolving dependencies...");
            try
            {
                ResolveDependencies();
            }
            catch (Exception ex)
            {
                utils.Logger.LogError(ex, "Encountered an error during dependency resolving. Shutting down...");
                typeof(Program).GetField("_restartOnShutdown", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, false);
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
        private void ResolveDependencies()
        {
            var entryAssembly = Assembly.GetEntryAssembly()!;
            var alc = AssemblyLoadContext.GetLoadContext(entryAssembly)!;

            var myAssembly = Assembly.GetExecutingAssembly();
            var myDir = Path.GetDirectoryName(myAssembly.Location)!;
            var pluginsDir = Directory.GetParent(myDir)!.FullName;

            resolver.ResolveAll().Wait();

            // make sure this happens after resolving dependencies, since it might have installed new plugins
            var pluginDirs = Directory.EnumerateDirectories(pluginsDir, "*.*", SearchOption.TopDirectoryOnly);

            foreach (var pluginDir in pluginDirs)
            {
                var loaderManifest = pluginHelper.ReadLoaderManifest(pluginDir);
                if (loaderManifest == null) continue;

                if (loaderManifest.LoadControl != "EARLY") continue;

                // TODO: handle non-early load plugins, and handle dependencies

                // now we take control of the plugin
                var localPlugin = pluginHelper.ReadLocalPlugin(pluginDir);

                var dllFilePaths = pluginHelper.GetLoaderPluginDLLs(localPlugin, loaderManifest);
                var jlStubPaths = dllFilePaths.Where(path => Path.GetFileName(path) == LoaderStubName + ".dll");

                if (jlStubPaths.Count() != 1)
                {
                    utils.Logger.LogError("Failed to load plugin {Path}; encountered {Count} JellyfinLoader stubs, expected 1. Disabling plugin.", pluginDir, jlStubPaths.Count());
                    localPlugin.Manifest.Status = PluginStatus.Disabled;
                    pluginHelper.SaveManifests(pluginDir, localPlugin.Manifest, loaderManifest);
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
                                }
                                else
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
                        utils.Logger.LogError(ex, "Failed to load assembly {Path}. Disabling plugin.", dllPath);
                        localPlugin.Manifest.Status = PluginStatus.Disabled;
                        pluginHelper.SaveManifests(pluginDir, localPlugin.Manifest, loaderManifest);
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
                        utils.Logger.LogError(ex, "Failed to load assembly {Path}. Unknown exception was thrown. Disabling plugin", assembly.Location);
                        localPlugin.Manifest.Status = PluginStatus.Disabled;
                        pluginHelper.SaveManifests(pluginDir, localPlugin.Manifest, loaderManifest);
                        loadedAll = false;
                        break;
                    }
                }

                if (!loadedAll) continue;

                foreach (var earlyLoadPluginHandlerType in earlyLoadPluginHandlerTypes)
                {
                    earlyLoadPluginInstances.Add(Activator.CreateInstance(earlyLoadPluginHandlerType)!);
                }

                localPlugin.Manifest.Status = PluginStatus.Active;
                pluginHelper.SaveManifests(pluginDir, localPlugin.Manifest, loaderManifest);
            }
        }

        internal Assembly? LoadFromAssemblyPath(string assemblyPath)
        {
            var isEarlyLoaded = earlyLoadedAssemblies.TryGetValue(assemblyPath, out var earlyAssembly);

            if (isEarlyLoaded)
            {
                utils.Logger.LogInformation("Returning early loaded assembly {assemblyPath}", assemblyPath);
                return earlyAssembly!;
            }

            return null;
        }

        /// <summary>
        /// Module initializer that makes sure harmony is loaded whenever the assembly is loaded so we don't get any type load errors.
        /// </summary>
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
            if (_bootstrapRan || _instance != null) return;
            _bootstrapRan = true;
            _instance = new JellyfinLoader();
        }
    }
}
