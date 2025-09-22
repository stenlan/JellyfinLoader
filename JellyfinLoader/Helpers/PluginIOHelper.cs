using Emby.Server.Implementations.Library;
using JellyfinLoader.AssemblyLoading;
using JellyfinLoader.Models;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Updates;
using Microsoft.Extensions.Logging;
using System.IO;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace JellyfinLoader.Helpers
{
    internal class PluginIOHelper(Utils utils)
    {
        private const string _pluginMetaFileName = "meta.json";
        private const string _loaderMetaFileName = "loader.json";

        // PluginManager#TryGetPluginDlls
        public (string stubPath, IEnumerable<string> dllPaths) GetJLAwarePluginDLLs(string pluginPath, PluginManifest pluginManifest, LoaderPluginManifest loaderManifest, ILogger? logger = null)
        {
            ArgumentNullException.ThrowIfNull(nameof(pluginManifest));
            ArgumentNullException.ThrowIfNull(nameof(loaderManifest));
            ArgumentNullException.ThrowIfNull(nameof(pluginPath));

            var pluginDlls = Directory.GetFiles(pluginPath, "*.dll", SearchOption.AllDirectories);

            // should never happen and it safeguarded against in JellyfinLoader.cs, but just to be sure.
            if (pluginManifest.Assemblies.Count != 1)
            {
                throw new InvalidOperationException($"Error while discovering DLLs for the plugin at {pluginPath}: Its meta.json does not contain exactly 1 DLL in its \"assemblies\" entry. It should contain just the JellyfinLoaderStub assembly, its loader.json's \"assemblies\" entry can optionally contain an actual assembly whitelist (or be left blank).");
            }

            var stubEntry = pluginManifest.Assemblies.First();
            var stubPath = Path.Combine(pluginPath, stubEntry).Canonicalize();

            // use loaderManifest.Assemblies instead of pluginManifest.Assemblies
            if (pluginDlls.Length > 0 && loaderManifest.Assemblies.Count > 0)
            {
                (logger ?? utils.Logger).LogInformation("Registering whitelisted assemblies for plugin \"{Plugin}\"...", pluginManifest.Name);

                var canonicalizedPaths = new List<string>();
                foreach (var path in loaderManifest.Assemblies)
                {
                    var canonicalized = Path.Combine(pluginPath, path).Canonicalize();

                    // Ensure we stay in the plugin directory.
                    if (!canonicalized.StartsWith(pluginPath.NormalizePath(), StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException($"Assembly path {path} is not inside the plugin directory.");
                    } else if (canonicalized == stubPath)
                    {
                        throw new InvalidOperationException($"Plugin at {pluginPath} included the JellyfinLoaderStub inside its loader.json's \"assemblies\" entry. For JellyfinLoader plugins, the JellyfinLoaderStub should be the one and only entry in the meta.json file, while its loader.json's \"assemblies\" entry can optionally contain an actual assembly whitelist (or be left blank).");
                    }

                    canonicalizedPaths.Add(canonicalized);
                }

                var intersected = pluginDlls.Intersect(canonicalizedPaths);

                if (intersected.Count() != canonicalizedPaths.Count)
                {
                    throw new InvalidOperationException($"Plugin {pluginManifest.Name} contained assembly paths that were not found in the directory.");
                }

                return (stubPath, intersected);
            }
            else
            {
                // No whitelist, default to loading all DLLs in plugin directory except the stub.
                return (stubPath, pluginDlls.Where(path => path != stubPath));
            }
        }

        // PluginManager#TryGetPluginDlls
        public IReadOnlyList<string> GetPluginDLLs(string pluginPath, PluginManifest pluginManifest, ILogger? logger = null)
        {
            ArgumentNullException.ThrowIfNull(nameof(pluginManifest));
            ArgumentNullException.ThrowIfNull(nameof(pluginPath));

            var pluginDlls = Directory.GetFiles(pluginPath, "*.dll", SearchOption.AllDirectories);

            if (pluginDlls.Length > 0 && pluginManifest.Assemblies.Count > 0)
            {
                (logger ?? utils.Logger).LogInformation("Registering whitelisted assemblies for plugin \"{Plugin}\"...", pluginManifest.Name);

                var canonicalizedPaths = new List<string>();
                foreach (var path in pluginManifest.Assemblies)
                {
                    var canonicalized = Path.Combine(pluginPath, path).Canonicalize();

                    // Ensure we stay in the plugin directory.
                    if (!canonicalized.StartsWith(pluginPath.NormalizePath(), StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException($"Assembly path {path} is not inside the plugin directory.");
                    }

                    canonicalizedPaths.Add(canonicalized);
                }

                var intersected = pluginDlls.Intersect(canonicalizedPaths).ToList();

                if (intersected.Count != canonicalizedPaths.Count)
                {
                    throw new InvalidOperationException($"Plugin {pluginManifest.Name} contained assembly paths that were not found in the directory.");
                }

                return intersected;
            }
            else
            {
                // No whitelist, default to loading all DLLs in plugin directory.
                return pluginDlls;
            }
        }

        /// <summary>
        /// Helper function for the PluginManager#TryGetPluginDLLs hook
        /// </summary>
        internal bool TryGetPluginDLLs(LocalPlugin plugin, ref IReadOnlyList<string> whitelistedDlls, ILogger logger)
        {
            var pluginPath = plugin.Path;
            var loaderManifest = ReadLoaderManifest(pluginPath);
            try
            {
                if (loaderManifest != null && plugin.Manifest.Assemblies.Count != 1)
                {
                    utils.Logger.LogWarning("Despite containing a loader.json file, the plugin at {pluginPath} will not be treated as a JellyfinLoader plugin because its meta.json does not contain exactly 1 DLL in its \"assemblies\" entry. It should contain just the JellyfinLoaderStub assembly, its loader.json's \"assemblies\" entry can optionally contain an actual assembly whitelist (or be left blank).", plugin.Path);
                    loaderManifest = null;
                }

                if (loaderManifest == null)
                {
                    whitelistedDlls = GetPluginDLLs(pluginPath, plugin.Manifest, logger);
                } else
                {
                    var (stubPath, dlls) = GetJLAwarePluginDLLs(pluginPath, plugin.Manifest, loaderManifest, logger);
                    whitelistedDlls = [..dlls];
                }
                return true;
            } catch
            {
                return false;
            }
        }

        public bool SaveManifests(string path, PluginManifest pluginManifest, LoaderPluginManifest loaderManifest)
        {
            try
            {
                File.WriteAllText(Path.Combine(path, _loaderMetaFileName), JsonSerializer.Serialize(loaderManifest, utils.JsonOptions));
                File.WriteAllText(Path.Combine(path, _pluginMetaFileName), JsonSerializer.Serialize(pluginManifest, utils.JsonOptions));
                return true;
            }
            catch (ArgumentException e)
            {
                return false;
            }
        }

        public bool SavePluginManifest(PluginManifest manifest, string path)
        {
            try
            {
                var data = JsonSerializer.Serialize(manifest, utils.JsonOptions);
                File.WriteAllText(Path.Combine(path, _pluginMetaFileName), data);
                return true;
            }
            catch (ArgumentException e)
            {
                return false;
            }
        }

        public PluginManifest? ReadPluginManifest(string dir)
        {
            var metaFile = Path.Combine(dir, _pluginMetaFileName);
            if (!File.Exists(metaFile)) return null;

            try
            {
                byte[] data = File.ReadAllBytes(metaFile);
                var manifest = JsonSerializer.Deserialize<PluginManifest>(data, utils.JsonOptions);

                return manifest;
            }
            catch
            {
                utils.Logger.LogError("Error reading plugin manifest at {metaFile}.", metaFile);
                return null;
            }
        }

        public LoaderPluginManifest? ReadLoaderManifest(string dir)
        {
            var metaFile = Path.Combine(dir, _loaderMetaFileName);
            if (!File.Exists(metaFile)) return null;

            try
            {
                byte[] data = File.ReadAllBytes(metaFile);
                var manifest = JsonSerializer.Deserialize<LoaderPluginManifest>(data, utils.JsonOptions);

                return manifest;
            }
            catch
            {
                utils.Logger.LogError("Error reading loader manifest at {metaFile}.", metaFile);
                return null;
            }
        }

        public bool SaveLoaderManifest(LoaderPluginManifest manifest, string path)
        {
            try
            {
                var data = JsonSerializer.Serialize(manifest, utils.JsonOptions);
                File.WriteAllText(Path.Combine(path, _loaderMetaFileName), data);
                return true;
            }
            catch (ArgumentException e)
            {
                return false;
            }
        }

        // InstallationManager#GetPackages
        public async Task<PackageInfo[]> GetPackages(string manifestUrl, bool filterIncompatible, CancellationToken cancellationToken = default)
        {
            try
            {
                PackageInfo[]? packages = await utils.HttpClient
                        .GetFromJsonAsync<PackageInfo[]>(new Uri(manifestUrl), utils.JsonOptions, cancellationToken).ConfigureAwait(false);

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
                            targetAbi = utils.MinimumVersion;
                        }

                        // Only show plugins that are greater than or equal to targetAbi.
                        if (utils.AppVersion >= targetAbi)
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
                utils.Logger.LogError(ex, "Cannot locate the plugin manifest {Manifest}", manifestUrl);
                return Array.Empty<PackageInfo>();
            }
            catch (JsonException ex)
            {
                utils.Logger.LogError(ex, "Failed to deserialize the plugin manifest retrieved from {Manifest}", manifestUrl);
                return Array.Empty<PackageInfo>();
            }
            catch (UriFormatException ex)
            {
                utils.Logger.LogError(ex, "The URL configured for the plugin repository manifest URL is not valid: {Manifest}", manifestUrl);
                return Array.Empty<PackageInfo>();
            }
            catch (HttpRequestException ex)
            {
                utils.Logger.LogError(ex, "An error occurred while accessing the plugin manifest: {Manifest}", manifestUrl);
                return Array.Empty<PackageInfo>();
            }
        }

        // PluginManager#LoadManifest
        internal LocalPlugin ReadLocalPlugin(string dir)
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
                    manifest = JsonSerializer.Deserialize<PluginManifest>(data, utils.JsonOptions);
                }
                catch (IOException ex)
                {
                    utils.Logger.LogError(ex, "Error reading file {Path}.", dir);
                }
                catch (JsonException ex)
                {
                    utils.Logger.LogError(ex, "Error deserializing {Json}.", Encoding.UTF8.GetString(data));
                }

                if (manifest is not null)
                {
                    if (!Version.TryParse(manifest.TargetAbi, out var targetAbi))
                    {
                        targetAbi = utils.MinimumVersion;
                    }

                    if (!Version.TryParse(manifest.Version, out version))
                    {
                        manifest.Version = utils.MinimumVersion.ToString();
                    }

                    return new LocalPlugin(dir, utils.AppVersion >= targetAbi, manifest);
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
                version = Version.TryParse(dir.AsSpan()[(versionIndex + 1)..], out Version? parsedVersion) ? parsedVersion : utils.AppVersion;
            }
            else
            {
                // Un-versioned folder - Add it under the path name and version it suitable for this instance.
                version = utils.AppVersion;
            }

            // Auto-create a plugin manifest, so we can disable it, if it fails to load.
            manifest = new PluginManifest
            {
                Status = PluginStatus.Active,
                Name = metafile,
                AutoUpdate = false,
                Id = metafile.GetMD5(),
                TargetAbi = utils.AppVersion.ToString(),
                Version = version.ToString()
            };

            return new LocalPlugin(dir, true, manifest);
        }

        // InstallationManager#PerformPackageInstallation
        public async Task<string> PerformPackageInstallation(InstallationInfo package, PluginStatus status)
        {
            if (!Path.GetExtension(package.SourceUrl.AsSpan()).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                utils.Logger.LogError("Only zip packages are supported. {SourceUrl} is not a zip archive.", package.SourceUrl);
                throw new InvalidDataException("Non-zip package encountered.");
            }

            // Always override the passed-in target (which is a file) and figure it out again
            string targetDir = Path.Combine(utils.PluginsPath, package.Name);

            using var response = await utils.HttpClient
                .GetAsync(new Uri(package.SourceUrl)).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);

            var hash = Convert.ToHexString(await MD5.HashDataAsync(stream).ConfigureAwait(false));
            if (!string.Equals(package.Checksum, hash, StringComparison.OrdinalIgnoreCase))
            {
                utils.Logger.LogError(
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
            await utils.PluginManager.PopulateManifest(package.PackageInfo, package.Version, targetDir, status).ConfigureAwait(false);

            utils.PluginManager.ImportPluginFrom(targetDir);

            return targetDir;
        }
    }
}
