using Emby.Server.Implementations;
using Emby.Server.Implementations.Library;
using Emby.Server.Implementations.Plugins;
using HarmonyLib;
using Jellyfin.Extensions.Json;
using Jellyfin.Extensions.Json.Converters;
using Jellyfin.Server;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Plugins;
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
        private const string _pluginMetaFileName = "meta.json";
        private const string _loaderMetaFileName = "loader.json";
        private static readonly Version _minimumVersion = new Version(0, 0, 0, 1);
        private static readonly Version _appVersion = typeof(ApplicationHost).Assembly.GetName().Version!;

        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions(JsonDefaults.Options)
        {
            WriteIndented = true
        };
        private static bool _coldStart = true;
        private static ILogger? _loggerInstance;
        private static Dictionary<string, Assembly> earlyLoadedAssemblies = new Dictionary<string, Assembly>();
        private static List<IEarlyLoadPlugin> earlyLoadPluginInterfaces = new();

        private static ILogger _logger {
            get {
                if (_loggerInstance != null) return _loggerInstance;

                return _loggerInstance = ((SerilogLoggerFactory) typeof(Program).GetField("_loggerFactory", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)).CreateLogger("JellyfinLoader");
            }
        }

        /// <summary>
        /// Method called as early as possible by the disk-patched DLL, or once by our stub before soft restarting. Our DLL will be loaded into the main assembly load context,
        /// but nothing else (dnlib, harmony), so we should take care of that ourselves. This method is NOT called again after a soft restart,
        /// so we are free to load any DLLs into the main assembly load context and blindly apply runtime patches.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Bootstrap()
        {
            // _logger.LogInformation("In bootstrap.");

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

        private static void BootstrapStep2()
        {
            for (int a = _jsonOptions.Converters.Count - 1; a >= 0; a--)
            {
                if (_jsonOptions.Converters[a] is JsonGuidConverter convertor)
                {
                    _jsonOptions.Converters.Remove(convertor);
                    break;
                }
            }

            var myAssembly = Assembly.GetExecutingAssembly();
            var myDir = Path.GetDirectoryName(myAssembly.Location)!;
            var pluginsDir = Directory.GetParent(myDir)!.FullName;

            var pluginDirs = Directory.EnumerateDirectories(pluginsDir, "*.*", SearchOption.TopDirectoryOnly);
            var entryAssembly = Assembly.GetEntryAssembly()!;
            var alc = AssemblyLoadContext.GetLoadContext(entryAssembly)!;

            foreach (var pluginDir in pluginDirs)
            {
                var loaderManifest = ReadLoaderManifest(pluginDir);
                if (loaderManifest?.LoadControl != "EARLY") continue;

                // now we take control of the plugin
                var localPlugin = ReadLocalPlugin(pluginDir);

                if (!loaderManifest.Enabled) // disable the plugin in its own manifest too
                {
                    localPlugin.Manifest.Status = PluginStatus.Disabled;
                    SavePluginManifest(localPlugin.Manifest, pluginDir);
                    continue;
                }

                var pluginDllFiles = TryGetPluginDlls(localPlugin);
                var assemblies = new List<Assembly>(pluginDllFiles.Count);
                var loadedAll = true;

                foreach (var file in pluginDllFiles)
                {
                    try
                    {
                        assemblies.Add(alc.LoadFromAssemblyPath(file));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to load assembly {Path}. Unknown exception was thrown. Disabling plugin", file);
                        loaderManifest.Enabled = false;
                        SaveLoaderManifest(loaderManifest, pluginDir);

                        localPlugin.Manifest.Status = PluginStatus.Disabled;
                        SavePluginManifest(localPlugin.Manifest, pluginDir);
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
                        earlyLoadPluginHandlerTypes.AddRange(assemblyTypes.Where(type => type.IsAssignableTo(typeof(IEarlyLoadPlugin))));
                        earlyLoadedAssemblies.Add(assembly.Location, assembly);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to load assembly {Path}. Unknown exception was thrown. Disabling plugin", assembly.Location);
                        loaderManifest.Enabled = false;
                        SaveLoaderManifest(loaderManifest, pluginDir);

                        localPlugin.Manifest.Status = PluginStatus.Disabled;
                        SavePluginManifest(localPlugin.Manifest, pluginDir);
                        loadedAll = false;
                        break;
                    }
                }

                if (!loadedAll) continue;

                foreach (var earlyLoadPluginHandlerType in earlyLoadPluginHandlerTypes)
                {
                    earlyLoadPluginInterfaces.Add((IEarlyLoadPlugin)Activator.CreateInstance(earlyLoadPluginHandlerType));
                }

                localPlugin.Manifest.Status = PluginStatus.Active;
                SavePluginManifest(localPlugin.Manifest, pluginDir);
            }

            var harmony = new Harmony("com.github.stenlan.jellyfinloader");
            harmony.Patch(AccessTools.Method(typeof(AssemblyLoadContext), "LoadFromAssemblyPath"), prefix: new HarmonyMethod(LoadFromAssemblyPathHook));
            harmony.Patch(AccessTools.DeclaredMethod(typeof(Program), "StartServer"), prefix: new HarmonyMethod(StartServerHook));
        }

        internal static LocalPlugin ReadLocalPlugin(string dir)
        {
            Version? version;
            PluginManifest? manifest = null;
            var metafile = Path.Combine(dir, _pluginMetaFileName);
            if (File.Exists(metafile))
            {
                // Only path where this stays null is when File.ReadAllBytes throws an IOException
                byte[] data = null!;
                try
                {
                    data = File.ReadAllBytes(metafile);
                    manifest = JsonSerializer.Deserialize<PluginManifest>(data, _jsonOptions);
                }
                catch (IOException ex)
                {
                    _logger.LogError(ex, "Error reading file {Path}.", dir);
                }
                catch (JsonException ex)
                {
                    _logger.LogError(ex, "Error deserializing {Json}.", Encoding.UTF8.GetString(data));
                }

                if (manifest is not null)
                {
                    if (!Version.TryParse(manifest.TargetAbi, out var targetAbi))
                    {
                        targetAbi = _minimumVersion;
                    }

                    if (!Version.TryParse(manifest.Version, out version))
                    {
                        manifest.Version = _minimumVersion.ToString();
                    }

                    return new LocalPlugin(dir, _appVersion >= targetAbi, manifest);
                }
            }

            // No metafile, so lets see if the folder is versioned.
            // TODO: Phase this support out in future versions.
            metafile = dir.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)[^1];
            int versionIndex = dir.LastIndexOf('_');
            if (versionIndex != -1)
            {
                // Get the version number from the filename if possible.
                metafile = Path.GetFileName(dir[..versionIndex]);
                version = Version.TryParse(dir.AsSpan()[(versionIndex + 1)..], out Version? parsedVersion) ? parsedVersion : _appVersion;
            }
            else
            {
                // Un-versioned folder - Add it under the path name and version it suitable for this instance.
                version = _appVersion;
            }

            // Auto-create a plugin manifest, so we can disable it, if it fails to load.
            manifest = new PluginManifest
            {
                Status = PluginStatus.Active,
                Name = metafile,
                AutoUpdate = false,
                Id = metafile.GetMD5(),
                TargetAbi = _appVersion.ToString(),
                Version = version.ToString()
            };

            return new LocalPlugin(dir, true, manifest);
        }

        private static IReadOnlyList<string> TryGetPluginDlls(LocalPlugin plugin)
        {
            ArgumentNullException.ThrowIfNull(nameof(plugin));

            IReadOnlyList<string> pluginDlls = Directory.GetFiles(plugin.Path, "*.dll", SearchOption.AllDirectories);

            IReadOnlyList<string> whitelistedDlls = Array.Empty<string>();
            if (pluginDlls.Count > 0 && plugin.Manifest.Assemblies.Count > 0)
            {
                // _logger.LogInformation("Registering whitelisted assemblies for plugin \"{Plugin}\"...", plugin.Name);

                var canonicalizedPaths = new List<string>();
                foreach (var path in plugin.Manifest.Assemblies)
                {
                    var canonicalized = Path.Combine(plugin.Path, path).Canonicalize();

                    // Ensure we stay in the plugin directory.
                    if (!canonicalized.StartsWith(plugin.Path.NormalizePath(), StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException($"Assembly path {path} is not inside the plugin directory.");
                    }

                    canonicalizedPaths.Add(canonicalized);
                }

                var intersected = pluginDlls.Intersect(canonicalizedPaths).ToList();

                if (intersected.Count != canonicalizedPaths.Count)
                {
                    throw new InvalidOperationException($"Plugin {plugin.Name} contained assembly paths that were not found in the directory.");
                }

                return intersected;
            }
            else
            {
                // No whitelist, default to loading all DLLs in plugin directory.
                return pluginDlls;
            }
        }

        private static bool SavePluginManifest(PluginManifest manifest, string path)
        {
            try
            {
                var data = JsonSerializer.Serialize(manifest, _jsonOptions);
                File.WriteAllText(Path.Combine(path, _pluginMetaFileName), data);
                return true;
            }
            catch (ArgumentException e)
            {
                return false;
            }
        }

        private static LoaderPluginManifest? ReadLoaderManifest(string dir)
        {
            var metaFile = Path.Combine(dir, _loaderMetaFileName);
            if (!File.Exists(metaFile)) return null;

            try
            {
                byte[] data = File.ReadAllBytes(metaFile);
                var manifest = JsonSerializer.Deserialize<LoaderPluginManifest>(data, _jsonOptions);

                return manifest;
            }
            catch
            {
                _logger.LogError("Error reading loader manifest at {metaFile}.", metaFile);
                return null;
            }
        }

        private static bool SaveLoaderManifest(LoaderPluginManifest manifest, string path)
        {
            try
            {
                var data = JsonSerializer.Serialize(manifest, _jsonOptions);
                File.WriteAllText(Path.Combine(path, _loaderMetaFileName), data);
                return true;
            }
            catch (ArgumentException e)
            {
                return false;
            }
        }

        private static void StartServerHook()
        {
            _logger.LogInformation("Server starting (ColdStart = {ColdStart})...", _coldStart);
            foreach (var earlyLoadPluginInterface in earlyLoadPluginInterfaces)
            {
                earlyLoadPluginInterface.OnServerStart(_coldStart);
            }
            _coldStart = false;
        }


        private static bool LoadFromAssemblyPathHook(AssemblyLoadContext __instance, string assemblyPath, ref Assembly __result)
        {
            if (__instance is not PluginLoadContext instance) return true;

            var isEarlyLoaded = earlyLoadedAssemblies.TryGetValue(assemblyPath, out var earlyAssembly);

            if (isEarlyLoaded)
            {
                _logger.LogInformation("Early loading {assemblyPath}", assemblyPath);
                __result = earlyAssembly;
            }

            return !isEarlyLoaded; // only continue if not early loaded
        }
    }
}
