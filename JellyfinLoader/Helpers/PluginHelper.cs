using Emby.Server.Implementations.Library;
using JellyfinLoader.Models;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Updates;
using Microsoft.Extensions.Logging;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace JellyfinLoader.Helpers
{
    internal class PluginHelper
    {
        private const string _pluginMetaFileName = "meta.json";
        private const string _loaderMetaFileName = "loader.json";
        private static readonly Version _minimumVersion = new Version(0, 0, 0, 1);

        // PluginManager#TryGetPluginDlls
        public static IReadOnlyList<string> GetLoaderPluginDLLs(LocalPlugin plugin, LoaderPluginManifest loaderManifest)
        {
            ArgumentNullException.ThrowIfNull(nameof(plugin));

            var pluginDlls = Directory.GetFiles(plugin.Path, "*.dll", SearchOption.AllDirectories);
            var manifestAssemblies = new List<string>(plugin.Manifest.Assemblies);
            manifestAssemblies.AddRange(loaderManifest.Assemblies);

            // use loaderManifest.Assemblies for the whitelist check, but not for the actual returned assemblies
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
                File.WriteAllText(Path.Combine(path, _loaderMetaFileName), JsonSerializer.Serialize(loaderManifest, Utils.JsonOptions));
                File.WriteAllText(Path.Combine(path, _pluginMetaFileName), JsonSerializer.Serialize(pluginManifest, Utils.JsonOptions));
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
                var data = JsonSerializer.Serialize(manifest, Utils.JsonOptions);
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
                var manifest = JsonSerializer.Deserialize<PluginManifest>(data, Utils.JsonOptions);

                return manifest;
            }
            catch
            {
                Utils.Logger.LogError("Error reading plugin manifest at {metaFile}.", metaFile);
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
                var manifest = JsonSerializer.Deserialize<LoaderPluginManifest>(data, Utils.JsonOptions);

                return manifest;
            }
            catch
            {
                Utils.Logger.LogError("Error reading loader manifest at {metaFile}.", metaFile);
                return null;
            }
        }

        public static bool SaveLoaderManifest(LoaderPluginManifest manifest, string path)
        {
            try
            {
                var data = JsonSerializer.Serialize(manifest, Utils.JsonOptions);
                File.WriteAllText(Path.Combine(path, _loaderMetaFileName), data);
                return true;
            }
            catch (ArgumentException e)
            {
                return false;
            }
        }

        // InstallationManager#GetPackages
        public static async Task<PackageInfo[]> GetPackages(string manifestUrl, bool filterIncompatible, CancellationToken cancellationToken = default)
        {
            try
            {
                PackageInfo[]? packages = await Utils.HttpClient
                        .GetFromJsonAsync<PackageInfo[]>(new Uri(manifestUrl), Utils.JsonOptions, cancellationToken).ConfigureAwait(false);

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
                        if (Utils.AppVersion >= targetAbi)
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
                Utils.Logger.LogError(ex, "Cannot locate the plugin manifest {Manifest}", manifestUrl);
                return Array.Empty<PackageInfo>();
            }
            catch (JsonException ex)
            {
                Utils.Logger.LogError(ex, "Failed to deserialize the plugin manifest retrieved from {Manifest}", manifestUrl);
                return Array.Empty<PackageInfo>();
            }
            catch (UriFormatException ex)
            {
                Utils.Logger.LogError(ex, "The URL configured for the plugin repository manifest URL is not valid: {Manifest}", manifestUrl);
                return Array.Empty<PackageInfo>();
            }
            catch (HttpRequestException ex)
            {
                Utils.Logger.LogError(ex, "An error occurred while accessing the plugin manifest: {Manifest}", manifestUrl);
                return Array.Empty<PackageInfo>();
            }
        }

        // PluginManager#LoadManifest
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
                    manifest = JsonSerializer.Deserialize<PluginManifest>(data, Utils.JsonOptions);
                }
                catch (IOException ex)
                {
                    Utils.Logger.LogError(ex, "Error reading file {Path}.", dir);
                }
                catch (JsonException ex)
                {
                    Utils.Logger.LogError(ex, "Error deserializing {Json}.", Encoding.UTF8.GetString(data));
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

                    return new LocalPlugin(dir, Utils.AppVersion >= targetAbi, manifest);
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
                version = Version.TryParse(dir.AsSpan()[(versionIndex + 1)..], out Version? parsedVersion) ? parsedVersion : Utils.AppVersion;
            }
            else
            {
                // Un-versioned folder - Add it under the path name and version it suitable for this instance.
                version = Utils.AppVersion;
            }

            // Auto-create a plugin manifest, so we can disable it, if it fails to load.
            manifest = new PluginManifest
            {
                Status = PluginStatus.Active,
                Name = metafile,
                AutoUpdate = false,
                Id = metafile.GetMD5(),
                TargetAbi = Utils.AppVersion.ToString(),
                Version = version.ToString()
            };

            return new LocalPlugin(dir, true, manifest);
        }

        // InstallationManager#PerformPackageInstallation
        public static async Task<PackageInstallationResult> PerformPackageInstallation(InstallationInfo package, PluginStatus status)
        {
            if (!Path.GetExtension(package.SourceUrl.AsSpan()).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                Utils.Logger.LogError("Only zip packages are supported. {SourceUrl} is not a zip archive.", package.SourceUrl);
                throw new InvalidDataException("Non-zip package encountered.");
            }

            // Always override the passed-in target (which is a file) and figure it out again
            string targetDir = Path.Combine(Utils.PluginsPath, package.Name);

            using var response = await Utils.HttpClient
                .GetAsync(new Uri(package.SourceUrl)).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);

            var hash = Convert.ToHexString(await MD5.HashDataAsync(stream).ConfigureAwait(false));
            if (!string.Equals(package.Checksum, hash, StringComparison.OrdinalIgnoreCase))
            {
                Utils.Logger.LogError(
                    "The checksums didn't match while installing {Package}, expected: {Expected}, got: {Received}",
                    package.Name,
                    package.Checksum,
                    hash);
                throw new InvalidDataException("The checksum of the received data doesn't match.");
            }

            // Version folder as they cannot be overwritten in Windows.
            targetDir += "_" + package.Version;

            if (Directory.Exists(targetDir))
            {
                try
                {
                    Directory.Delete(targetDir, true);
                }
#pragma warning disable CA1031 // Do not catch general exception types
                catch
#pragma warning restore CA1031 // Do not catch general exception types
                {
                    // Ignore any exceptions.
                }
            }

            stream.Position = 0;
            ZipFile.ExtractToDirectory(stream, targetDir, true);

            // Ensure we create one or populate existing ones with missing data.
            await Utils.PluginManager.PopulateManifest(package.PackageInfo, package.Version, targetDir, status).ConfigureAwait(false);

            Utils.PluginManager.ImportPluginFrom(targetDir);

            return new PackageInstallationResult(ReadLoaderManifest(targetDir)?.Dependencies ?? [], targetDir, ReadLocalPlugin(targetDir));
        }
    }
}
