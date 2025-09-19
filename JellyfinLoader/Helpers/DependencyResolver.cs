using JellyfinLoader.Models;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Updates;
using Microsoft.Extensions.Logging;
using System.Text;

namespace JellyfinLoader.Helpers
{
    internal class DependencyResolver
    {
        // key is dependency Guid
        private Dictionary<Guid, List<InternalDependencyInfo>> _dependencies = [];

        // key is plugin Guid
        private Dictionary<Guid, HashSet<InstalledPluginInfo>> _installedPlugins = [];

        private readonly string _pluginsDirectory;

        public DependencyResolver(string pluginsDirectory) {
            _pluginsDirectory = pluginsDirectory;
        }

        public async Task ResolveAll()
        {
            var pluginDirs = Directory.EnumerateDirectories(_pluginsDirectory, "*.*", SearchOption.TopDirectoryOnly);

            // TODO: more thoroughly handle disabled states, conflicting versions, missing meta.jsons, etc

            // first, discover all installed plugins and dependencies
            foreach (var pluginDir in pluginDirs)
            {
                var pluginManifest = PluginHelper.ReadPluginManifest(pluginDir);
                if (pluginManifest == null) continue;

                var validVersion = Version.TryParse(pluginManifest.Version, out var pluginVersion);

                if (!validVersion)
                {
                    Utils.Logger.LogWarning("Plugin at {pluginDir} has an invalid version in its meta.json: {ver}. JellyfinLoader will not consider it as a dependency nor a dependent.", pluginDir, pluginManifest.Version);
                    continue;
                }

                var hasEntry = _installedPlugins.TryGetValue(pluginManifest.Id, out var pluginInfos);
                if (!hasEntry)
                {
                    _installedPlugins[pluginManifest.Id] = pluginInfos = [];
                }

                pluginInfos!.Add(new InstalledPluginInfo(pluginManifest.Id, pluginVersion!, pluginManifest.Status, pluginManifest.Name, pluginDir));

                var loaderManifest = PluginHelper.ReadLoaderManifest(pluginDir);
                if (loaderManifest == null) continue;

                // don't resolve or install dependencies for inactive plugins
                if (pluginManifest.Status != PluginStatus.Active) continue;

                if (loaderManifest.Dependencies.Any(d => d.Versions.Count > 1))
                {
                    throw new DependencyResolverException("Currently, only a single version per dependency is supported.");
                }

                loaderManifest.Dependencies.ForEach(d => {
                    var hasEntry = _dependencies.TryGetValue(d.ID, out var dependencyInfos);
                    if (!hasEntry)
                    {
                        _dependencies[d.ID] = dependencyInfos = [];
                    }
                    dependencyInfos!.Add(new InternalDependencyInfo(d.Manifest, d.ID, d.Versions, pluginManifest.Id));
                });
            }

            foreach (var pluginInfos in _installedPlugins.Values)
            {
                var activeVersions = pluginInfos.Where(pluginInfo => pluginInfo.Status == PluginStatus.Active);

                if (activeVersions.Count() > 1)
                {
                    var aVersion = activeVersions.First();
                    Utils.Logger.LogWarning("Found multiple enabled versions of plugin with ID {pluginID} and possible name {name} at paths:\n{paths}", aVersion.ID, aVersion.Name, string.Join("; ", activeVersions));
                }
            }

            List<DependencyInfo> flatDependencies;

            // attempt to install missing dependencies
            while ((flatDependencies = ResolveFlat()).Count > 0)
            {
                // TODO: smart dependency resolving:
                // Dependencies might be required by multiple different plugins, and thus there might be several entries for the same dependency, with possibly different required versions.
                // If they don't overlap, we are done and we cannot resolve the dependencies.
                // If they do overlap, there might be multiple candidate versions for a dependency, we need to find out which one to install.
                // We might be tempted to just pick a random one that satisfies all current dependencies, but it itself might then have dependencies that might
                // conflict with others, etc. So we first need to build a full dependency tree before persisting any installs.

                var installationResults = await Task.WhenAll(flatDependencies.Select(InstallDependency));
                _dependencies.Clear();

                foreach (var installationResult in installationResults)
                {
                    var hasEntry = _installedPlugins.TryGetValue(installationResult.LocalPlugin.Id, out var pluginInfos);
                    if (!hasEntry)
                    {
                        _installedPlugins[installationResult.LocalPlugin.Id] = pluginInfos = [];
                    }
                    pluginInfos!.Add(new InstalledPluginInfo(installationResult.LocalPlugin.Id,
                                                             installationResult.LocalPlugin.Version,
                                                             installationResult.LocalPlugin.Manifest.Status,
                                                             installationResult.LocalPlugin.Name,
                                                             installationResult.InstallDir));
                    
                    installationResult.NewDependencies.ForEach(d => {
                        var hasEntry = _dependencies.TryGetValue(d.ID, out var dependencyInfos);
                        if (!hasEntry)
                        {
                            _dependencies[d.ID] = dependencyInfos = [];
                        }
                        dependencyInfos!.Add(new InternalDependencyInfo(d.Manifest, d.ID, d.Versions, installationResult.LocalPlugin.Id));
                    });
                }
            }
        }

        private List<DependencyInfo> ResolveFlat()
        {
            // keep only missing dependencies, and flatten them to candidates?

            var flattenedDependencies = new List<DependencyInfo>();
            foreach (var (dependencyId, dependencyInfos) in _dependencies)
            {
                var isPluginInstalled = _installedPlugins.TryGetValue(dependencyId, out var pluginVersions);

                if (!isPluginInstalled)
                {
                    flattenedDependencies.Add(FindCandidates(dependencyId));
                    continue;
                }

                IEnumerable<InstalledPluginInfo> matchingVersions = pluginVersions!.Where(version => dependencyInfos.All(dependencyInfo => dependencyInfo.Versions.Contains(version.Version)));

                if (matchingVersions.Any(version => version.Status == PluginStatus.Active)) // a matching version is installed and enabled
                {
                    continue;
                }

                if (matchingVersions.Any()) // a matching version is installed, but disabled
                {
                    throw new DependencyResolverException($"A dependency plugin {matchingVersions.First().Name} was found, but not enabled!");
                }

                IEnumerable<InstalledPluginInfo> enabledVersions = pluginVersions!.Where(version => version.Status == PluginStatus.Active);

                if (!enabledVersions.Any()) // no matching versions, but none are enabled either, so we can just install a valid one
                {
                    flattenedDependencies.Add(FindCandidates(dependencyId));
                    continue;
                }

                // no matching version, but different enabled version
                // TODO: specify which versions are required by which dependent(s)
                throw new DependencyResolverException($"A dependency plugin {enabledVersions.First().Name} of version {enabledVersions.First().Version} was found, while a different one was required!");
            }

            return flattenedDependencies;
        }

        // find candidates, which is the intersection of all supported versions
        private DependencyInfo FindCandidates(Guid dependencyId)
        {
            var dependencyInfos = _dependencies[dependencyId];
            var versionCandidates = new HashSet<Version>(dependencyInfos[0].Versions);
            for (int i = 1; i < dependencyInfos.Count; i++)
            {
                var versionFilter = new HashSet<Version>(dependencyInfos[i].Versions);
                versionCandidates.RemoveWhere(candidate => !versionFilter.Contains(candidate));
            }

            if (versionCandidates.Count == 0)
            {
                StringBuilder exceptionString = new();
                exceptionString.AppendFormat(null, "Could not resolve a satisfactory version for plugin with ID {0}, since its dependents have no overlapping versions:\n", dependencyId);
                exceptionString.AppendJoin("\n", dependencyInfos.Select(i => $"Plugin {_installedPlugins[i.Dependent]!.First().Name} requires any of: {string.Join("; ", i.Versions)}"));
                throw new DependencyResolverException(exceptionString.ToString());
            }

            var manifestUrl = dependencyInfos[0].Manifest;
            if (dependencyInfos.Any(dependencyInfo => dependencyInfo.Manifest != manifestUrl))
            {
                StringBuilder exceptionString = new();
                exceptionString.AppendFormat(null, "Could not resolve a satisfactory version for plugin with ID {0}, since its dependents have differing manifest URLs:\n", dependencyId);
                exceptionString.AppendJoin("\n", dependencyInfos.Select(i => $"Plugin {_installedPlugins[i.Dependent]!.First().Name} specifies: {i.Manifest}"));
                throw new DependencyResolverException(exceptionString.ToString());
            }

            return new DependencyInfo()
            {
                ID = dependencyId,
                Manifest = manifestUrl,
                Versions = [.. versionCandidates]
            };
        }

        // TODO: multi-level dependency resolving, abi compatibility checks
        private async Task<PackageInstallationResult> InstallDependency(DependencyInfo info)
        {
            var packages = await PluginHelper.GetPackages(info.Manifest, false);
            var package = packages.FirstOrDefault(package => package.Id == info.ID);

            if (package == null)
            {
                throw new DependencyResolverException($"Package with ID {info.ID} not found in manifest at {info.Manifest}.");
            }

            var version = package.Versions.FirstOrDefault(version => info.Versions.Contains(version.VersionNumber));

            if (version == null)
            {
                throw new DependencyResolverException($"Version {version} of package with ID {info.ID} not found in manifest at {info.Manifest}.");
            }

            return await PluginHelper.PerformPackageInstallation(new InstallationInfo()
            {
                Changelog = version.Changelog,
                Id = package.Id,
                Name = package.Name,
                Version = version.VersionNumber,
                SourceUrl = version.SourceUrl,
                Checksum = version.Checksum,
                PackageInfo = package
            }, PluginStatus.Active);
        }
    }
}
