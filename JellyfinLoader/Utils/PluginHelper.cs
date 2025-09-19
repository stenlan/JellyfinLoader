using Emby.Server.Implementations;
using Emby.Server.Implementations.Library;
using Jellyfin.Extensions.Json;
using Jellyfin.Extensions.Json.Converters;
using JellyfinLoader.Models;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Updates;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Mime;
using System.Text;
using System.Text.Json;

namespace JellyfinLoader.Utils
{
    internal class PluginHelper
    {
        private const string _pluginMetaFileName = "meta.json";
        private const string _loaderMetaFileName = "loader.json";
        private static readonly Version _minimumVersion = new Version(0, 0, 0, 1);
        private static readonly Version _appVersion = typeof(ApplicationHost).Assembly.GetName().Version!;
        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions(JsonDefaults.Options)
        {
            WriteIndented = true
        };

        // Jellyfin.Server.Startup
        private static HttpClient HttpClient = new(new SocketsHttpHandler() {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            AutomaticDecompression = DecompressionMethods.All,
            RequestHeaderEncodingSelector = (_, _) => Encoding.UTF8,
        });

        static PluginHelper() {
            for (int a = _jsonOptions.Converters.Count - 1; a >= 0; a--)
            {
                if (_jsonOptions.Converters[a] is JsonGuidConverter convertor)
                {
                    _jsonOptions.Converters.Remove(convertor);
                    break;
                }
            }

            HttpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(
                "Jellyfin-Server",
                _appVersion.ToString()
            ));
            // Jellyfin.Server.Startup
            HttpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json, 1.0));
            HttpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Xml, 0.9));
            HttpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.8));
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
                    JellyfinLoader.logger.LogError(ex, "Error reading file {Path}.", dir);
                }
                catch (JsonException ex)
                {
                    JellyfinLoader.logger.LogError(ex, "Error deserializing {Json}.", Encoding.UTF8.GetString(data));
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

        public static IReadOnlyList<string> GetLoaderPluginDLLs(LocalPlugin plugin, LoaderPluginManifest loaderManifest)
        {
            ArgumentNullException.ThrowIfNull(nameof(plugin));

            var pluginDlls = Directory.GetFiles(plugin.Path, "*.dll", SearchOption.AllDirectories);
            var manifestAssemblies = new List<string>(plugin.Manifest.Assemblies);
            manifestAssemblies.AddRange(loaderManifest.Assemblies);

            // user loaderManifest.Assemblies for the whitelist check, but not for the actual returned assemblies
            if (pluginDlls.Length > 0 && loaderManifest.Assemblies.Count > 0)
            {
                // _logger.LogInformation("Registering whitelisted assemblies for plugin \"{Plugin}\"...", plugin.Name);

                var canonicalizedPaths = new List<string>();
                foreach (var path in manifestAssemblies)
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
        public static bool SaveManifests(string path, PluginManifest pluginManifest, LoaderPluginManifest loaderManifest)
        {
            try
            {
                File.WriteAllText(Path.Combine(path, _loaderMetaFileName), JsonSerializer.Serialize(loaderManifest, _jsonOptions));
                File.WriteAllText(Path.Combine(path, _pluginMetaFileName), JsonSerializer.Serialize(pluginManifest, _jsonOptions));
                return true;
            }
            catch (ArgumentException e)
            {
                return false;
            }
        }

        public static bool SavePluginManifest(PluginManifest manifest, string path)
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

        public static PluginManifest? ReadPluginManifest(string dir)
        {
            var metaFile = Path.Combine(dir, _pluginMetaFileName);
            if (!File.Exists(metaFile)) return null;

            try
            {
                byte[] data = File.ReadAllBytes(metaFile);
                var manifest = JsonSerializer.Deserialize<PluginManifest>(data, _jsonOptions);

                return manifest;
            }
            catch
            {
                JellyfinLoader.logger.LogError("Error reading plugin manifest at {metaFile}.", metaFile);
                return null;
            }
        }

        public static LoaderPluginManifest? ReadLoaderManifest(string dir)
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
                JellyfinLoader.logger.LogError("Error reading loader manifest at {metaFile}.", metaFile);
                return null;
            }
        }

        public static bool SaveLoaderManifest(LoaderPluginManifest manifest, string path)
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

        public static async Task<PackageInfo[]> GetPackages(string manifestUrl, bool filterIncompatible, CancellationToken cancellationToken = default)
        {
            try
            {
                PackageInfo[]? packages = await HttpClient
                        .GetFromJsonAsync<PackageInfo[]>(new Uri(manifestUrl), _jsonOptions, cancellationToken).ConfigureAwait(false);

                if (packages is null)
                {
                    return Array.Empty<PackageInfo>();
                }

                // Store the repository and repository url with each version, as they may be spread apart.
                foreach (var entry in packages)
                {
                    for (int a = entry.Versions.Count - 1; a >= 0; a--)
                    {
                        var ver = entry.Versions[a];
                        ver.RepositoryName = entry.Name;
                        ver.RepositoryUrl = manifestUrl;

                        if (!filterIncompatible)
                        {
                            continue;
                        }

                        if (!Version.TryParse(ver.TargetAbi, out var targetAbi))
                        {
                            targetAbi = _minimumVersion;
                        }

                        // Only show plugins that are greater than or equal to targetAbi.
                        if (_appVersion >= targetAbi)
                        {
                            continue;
                        }

                        // Not compatible with this version so remove it.
                        entry.Versions.Remove(ver);
                    }
                }

                return packages;
            }
            catch (IOException ex)
            {
                JellyfinLoader.logger.LogError(ex, "Cannot locate the plugin manifest {Manifest}", manifestUrl);
                return Array.Empty<PackageInfo>();
            }
            catch (JsonException ex)
            {
                JellyfinLoader.logger.LogError(ex, "Failed to deserialize the plugin manifest retrieved from {Manifest}", manifestUrl);
                return Array.Empty<PackageInfo>();
            }
            catch (UriFormatException ex)
            {
                JellyfinLoader.logger.LogError(ex, "The URL configured for the plugin repository manifest URL is not valid: {Manifest}", manifestUrl);
                return Array.Empty<PackageInfo>();
            }
            catch (HttpRequestException ex)
            {
                JellyfinLoader.logger.LogError(ex, "An error occurred while accessing the plugin manifest: {Manifest}", manifestUrl);
                return Array.Empty<PackageInfo>();
            }
        }

        public static DependencyInfo[] InstallPlugin(DependencyInfo info)
        {
            throw new NotImplementedException();
        }
    }
}
