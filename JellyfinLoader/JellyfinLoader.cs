using HarmonyLib;
using JellyfinLoader.AssemblyLoading;
using JellyfinLoader.Helpers;
using JellyfinLoader.Hooks;
using Microsoft.Extensions.Logging;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace JellyfinLoader
{
    public class JellyfinLoader
    {       
        private static JellyfinLoader? _instance;
        public static JellyfinLoader Instance { get => _instance ?? throw new NullReferenceException("Attempted to access JellyfinLoader instance before its initialization."); }
        private static bool _bootstrapRan = false;
        
        private bool _coldStart = true;

        private readonly Utils utils;
        private readonly Harmony harmony;
        private readonly DependencyResolver resolver;
        internal readonly PluginIOHelper pluginIOHelper;
        internal readonly AssemblyLoader assemblyLoader;

        private JellyfinLoader()
        {
            utils = new Utils();
            utils.Logger.LogInformation("In bootstrap.");

            harmony = new Harmony("com.github.stenlan.jellyfinloader");
            // we ONLY apply the StartServer hook here, to ensure that any other "early" code is running in the same state regardless of
            // Bootstrap() being called by the disk-patched DLL or the trampoline
            harmony.PatchCategory(Assembly.GetExecutingAssembly(), nameof(StartServerHook));

            pluginIOHelper = new PluginIOHelper(utils);
            resolver = new DependencyResolver(utils, pluginIOHelper);
            assemblyLoader = new AssemblyLoader(utils, pluginIOHelper, resolver);
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
                harmony.PatchCategory(Assembly.GetExecutingAssembly(), nameof(TryGetPluginDLLsHook));
            }

            utils.Logger.LogInformation("Resolving dependencies...");
            try
            {
                resolver.ResolveAll().Wait();
            }
            catch (Exception ex)
            {
                utils.Logger.LogError(ex, "Encountered an error during dependency resolving. Shutting down...");
                return false;
            }

            assemblyLoader.OnServerStart();

            try
            {
                assemblyLoader.LoadEarlyPlugins(_coldStart);
            }
            catch (Exception ex)
            {
                utils.Logger.LogError(ex, "Encountered an error during loading of early plugins. Shutting down...");
                return false;
            }

            _coldStart = false;
            return true;
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
