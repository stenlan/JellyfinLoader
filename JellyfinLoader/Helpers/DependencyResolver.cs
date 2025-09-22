using JellyfinLoader.Models;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Updates;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using System.Text;

namespace JellyfinLoader.Helpers
{
    internal class DependencyResolver(Utils utils, PluginIOHelper pluginIOHelper)
    {
        // key is plugin Guid
        private Dictionary<Guid, List<InstalledPluginInfo>> _installedPlugins = [];

        // dependency plugin Guid to InternalDependencyInfo mapping
        private Dictionary<Guid, List<InternalDependencyInfo>> _allDependencies = [];

        private int _highestDepPoolID = Utils.MainDepPoolID;

        // sparse mapping of dependency pool ID to dependency pool
        // a dependency pool is a list of Guids that defines the order in which to load the plugins
        internal readonly Dictionary<int, List<Guid>> dependencyPools = [];

        // mapping of plugin ID to dep pool ID
        private readonly Dictionary<Guid, int> _pluginToPoolMap = [];

        public async Task ResolveAll()
        {
            // TODO: maybe use constructor or something
            _installedPlugins.Clear();
            _allDependencies.Clear();
            dependencyPools.Clear();
            _pluginToPoolMap.Clear();

            var pluginDirs = Directory.EnumerateDirectories(utils.PluginsPath, "*.*", SearchOption.TopDirectoryOnly);

            // TODO: more thoroughly handle disabled states, conflicting versions, missing meta.jsons, etc

            Dictionary<Guid, List<InternalDependencyInfo>> currentRoundDependencies = [];

            // first, discover all installed plugins and dependencies
            foreach (var pluginDir in pluginDirs)
            {
                var pluginManifest = pluginIOHelper.ReadPluginManifest(pluginDir);
                if (pluginManifest == null)
                {
                    utils.Logger.LogWarning("Plugin at {pluginDir} does not have a meta.json file. JellyfinLoader will not consider it as a dependency nor a dependent.", pluginDir);
                    continue;
                }

                var validVersion = Version.TryParse(pluginManifest.Version, out var pluginVersion);

                if (!validVersion)
                {
                    _installedPlugins.GetOrAdd(pluginManifest.Id, []).Add(new InstalledPluginInfo(pluginManifest, null, utils.MinimumVersion, pluginDir));
                    utils.Logger.LogWarning("Plugin at {pluginDir} has an invalid version in its meta.json: {ver}. JellyfinLoader will not consider it as a dependency nor a dependent.", pluginDir, pluginManifest.Version);
                    continue;
                }

                foreach (var dependencyInfo in AddInstalledPlugin(new InstalledPluginInfo(pluginManifest, pluginIOHelper.ReadLoaderManifest(pluginDir), pluginVersion!, pluginDir)))
                {
                    currentRoundDependencies.GetOrAdd(dependencyInfo.ID, []).Add(dependencyInfo);
                }
            }

            // make sure at most 1 version of any given plugin is enabled
            foreach (var pluginInfos in _installedPlugins.Values)
            {
                var activeVersions = pluginInfos.Where(pluginInfo => pluginInfo.Manifest.Status == PluginStatus.Active);

                if (activeVersions.Count() > 1)
                {
                    var maxVersion = activeVersions.MaxBy(v => v.Version)!;
                    utils.Logger.LogWarning("Found multiple enabled versions of plugin with ID {pluginID} and possible name {name} at paths:\n{paths}\nSuperceding all but the newest.", maxVersion.Manifest.Id, maxVersion.Manifest.Name, string.Join("\n", activeVersions));

                    foreach (var version in activeVersions)
                    {
                        if (version == maxVersion) continue;
                        ChangePluginState(version, PluginStatus.Superceded);
                    }
                }
            }

            List<DependencyInfo> missingDependencies;

            // attempt to install missing dependencies
            while ((missingDependencies = FilterAndFlatten(currentRoundDependencies)).Count > 0)
            {
                // TODO: smart dependency resolving:
                // Dependencies might be required by multiple different plugins, and thus there might be several entries for the same dependency, with possibly different required versions.
                // If they don't overlap, we are done and we cannot resolve the dependencies.
                // If they do overlap, there might be multiple candidate versions for a dependency, we need to find out which one to install.
                // We might be tempted to just pick a random one that satisfies all current dependencies, but it itself might then have dependencies that might
                // conflict with others, etc. So we first need to build a full dependency tree before persisting any installs.

                var newPlugins = await Task.WhenAll(missingDependencies.Select(InstallDependency));
                currentRoundDependencies.Clear();

                foreach (var newPlugin in newPlugins)
                {
                    foreach (var dependencyInfo in AddInstalledPlugin(newPlugin))
                    {
                        currentRoundDependencies.GetOrAdd(dependencyInfo.ID, []).Add(dependencyInfo);
                    }
                }
            }

            // now construct the final dependency pools
            foreach (var (pluginId, pluginInfos) in _installedPlugins)
            {
                // at this point we can assume there is either 0 or 1 enabled instances of a plugin
                var pluginInfo = pluginInfos.FirstOrDefault(info => info.Manifest.Status == PluginStatus.Active);
                if (pluginInfo is null) continue;

                AssignToDependencyPool(pluginInfo);
            }
        }

        public List<Guid> GetDependencyPool(Guid pluginID, out int poolID)
        {
            poolID = _pluginToPoolMap[pluginID];
            return dependencyPools[poolID];
        }

        public IEnumerable<InstalledPluginInfo> GetActivePlugins()
        {
            foreach (var (pluginId, pluginInfos) in _installedPlugins)
            {
                // at this point we can assume there is either 0 or 1 enabled instances of a plugin
                var pluginInfo = pluginInfos.FirstOrDefault(info => info.Manifest.Status == PluginStatus.Active);
                if (pluginInfo is null) continue;

                yield return pluginInfo;
            }
        }

        /// <summary>
        /// Gets the <see cref="InstalledPluginInfo"/> for the plugin with id <paramref name="pluginID"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public InstalledPluginInfo? GetPluginInfo(Guid pluginID)
        {
            return _installedPlugins[pluginID].FirstOrDefault(i => i.Manifest.Status == PluginStatus.Active);
        }

        /// <summary>
        /// Filters the given dependencies by only keeping ones that are not yet installed, then converts them to a flat list of
        /// <see cref="DependencyInfo"/>s to consider for installation. Resulting versions are an intersection of all the dependency versions per a given Guid.
        /// </summary>
        /// <param name="dependencies"></param>
        /// <returns></returns>
        /// <exception cref="DependencyResolverException"></exception>
        private List<DependencyInfo> FilterAndFlatten(Dictionary<Guid, List<InternalDependencyInfo>> dependencies)
        {
            var result = new List<DependencyInfo>();
            foreach (var (dependencyId, dependencyInfos) in dependencies)
            {
                var isPluginInstalled = _installedPlugins.TryGetValue(dependencyId, out var pluginVersions);

                if (!isPluginInstalled)
                {
                    result.Add(FindCandidates(dependencies, dependencyId));
                    continue;
                }

                IEnumerable<InstalledPluginInfo> matchingVersions = pluginVersions!.Where(version => dependencyInfos.All(dependencyInfo => dependencyInfo.Versions.Contains(version.Version)));

                if (matchingVersions.Any(version => version.Manifest.Status == PluginStatus.Active)) // a matching version is installed and enabled
                {
                    continue;
                }

                if (matchingVersions.Any()) // a matching version is installed, but disabled
                {
                    throw new DependencyResolverException($"A dependency plugin {matchingVersions.First().Manifest.Name} was found, but not enabled!");
                }

                IEnumerable<InstalledPluginInfo> enabledVersions = pluginVersions!.Where(version => version.Manifest.Status == PluginStatus.Active);

                if (!enabledVersions.Any()) // no matching versions, but none are enabled either, so we can just install a valid one
                {
                    result.Add(FindCandidates(dependencies, dependencyId));
                    continue;
                }

                // no matching version, but different enabled version
                // TODO: specify which versions are required by which dependent(s)
                // TODO: auto update?
                throw new DependencyResolverException($"A dependency plugin {enabledVersions.First().Manifest.Name} of version {enabledVersions.First().Manifest.Version} was found, while a different one was required!");
            }

            return result;
        }

        // find candidates, which is the intersection of all supported versions
        private DependencyInfo FindCandidates(Dictionary<Guid, List<InternalDependencyInfo>> dependencies, Guid dependencyId)
        {
            var dependencyInfos = dependencies[dependencyId];
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
                exceptionString.AppendJoin("\n", dependencyInfos.Select(i => $"Plugin {_installedPlugins[i.Dependent]![0].Manifest.Name} requires any of: {string.Join("; ", i.Versions)}"));
                throw new DependencyResolverException(exceptionString.ToString());
            }

            var manifestUrl = dependencyInfos[0].Manifest;
            if (dependencyInfos.Any(dependencyInfo => dependencyInfo.Manifest != manifestUrl))
            {
                StringBuilder exceptionString = new();
                exceptionString.AppendFormat(null, "Could not resolve a satisfactory version for plugin with ID {0}, since its dependents have differing manifest URLs:\n", dependencyId);
                exceptionString.AppendJoin("\n", dependencyInfos.Select(i => $"Plugin {_installedPlugins[i.Dependent]![0].Manifest.Name} specifies: {i.Manifest}"));
                throw new DependencyResolverException(exceptionString.ToString());
            }

            return new DependencyInfo()
            {
                ID = dependencyId,
                Manifest = manifestUrl,
                Versions = [.. versionCandidates]
            };
        }

        // TODO: detect circular dependency
        /// <summary>
        /// Assigns a plugin to a dependency pool, or returns the assigned dependency pool if this plugin has already been assigned to a dependency pool.
        /// </summary>
        private int AssignToDependencyPool(InstalledPluginInfo pluginInfo)
        {
            var pluginID = pluginInfo.Manifest.Id;

            var alreadyAssigned = _pluginToPoolMap.TryGetValue(pluginID, out var assignedID);
            if (alreadyAssigned) return assignedID;

            List<DependencyInfo> dependencies;

            // base case; no dependencies, so place ourself into our own, new dependency pool
            if (pluginInfo?.LoaderManifest == null || (dependencies = pluginInfo.LoaderManifest.Dependencies).Count == 0)
            {
                _highestDepPoolID++;

                dependencyPools[_highestDepPoolID] = [pluginID];
                _pluginToPoolMap[pluginID] = _highestDepPoolID;

                return _highestDepPoolID;
            }

            // get dependency pool ID of our first dependency, assigning it first if it had not yet been
            int depPoolID = AssignToDependencyPool(GetPluginInfo(dependencies[0].ID)!);
            var dependencyPool = dependencyPools[depPoolID];

            // merge all our other dependencies' dependency pools into the first one
            for (int i = 1; i < dependencies.Count; i++)
            {
                int sourceDepPoolID = AssignToDependencyPool(GetPluginInfo(dependencies[i].ID)!);
                if (sourceDepPoolID == depPoolID) continue;

                var sourceDepPool = dependencyPools[sourceDepPoolID];
                dependencyPools.Remove(sourceDepPoolID);

                dependencyPool.EnsureCapacity(dependencyPool.Count + sourceDepPool.Count);

                foreach (var dependencyID in sourceDepPool)
                {
                    dependencyPool.Add(dependencyID);
                    _pluginToPoolMap[dependencyID] = depPoolID;
                }
            }

            // finally, add ourselves to the end of this dependency pool
            dependencyPool.Add(pluginID);
            _pluginToPoolMap[pluginID] = depPoolID;

            return depPoolID;
        }

        // TODO: multi-level dependency resolving, abi compatibility checks
        private async Task<InstalledPluginInfo> InstallDependency(DependencyInfo info)
        {
            var packages = await pluginIOHelper.GetPackages(info.Manifest, false);
            var package = packages.FirstOrDefault(package => package.Id == info.ID)
                ?? throw new DependencyResolverException($"Package with ID {info.ID} not found in repository at {info.Manifest}.");

            var version = package.Versions.FirstOrDefault(version => info.Versions.Contains(version.VersionNumber))
                ?? throw new DependencyResolverException($"None of versions \"{string.Join(", ", info.Versions)}\" of package with ID {info.ID} were found in repository at {info.Manifest}.");

            var installDir = await pluginIOHelper.PerformPackageInstallation(new InstallationInfo()
            {
                Changelog = version.Changelog,
                Id = package.Id,
                Name = package.Name,
                Version = version.VersionNumber,
                SourceUrl = version.SourceUrl,
                Checksum = version.Checksum,
                PackageInfo = package
            }, PluginStatus.Active);

            var pluginManifest = pluginIOHelper.ReadPluginManifest(installDir) ?? throw new InvalidOperationException($"Failed to read plugin manifest for plugin {package.Name} immediately after installing.");

            if (pluginManifest.Status != PluginStatus.Active)
            {
                throw new InvalidOperationException($"Plugin {package.Name} status was {pluginManifest.Status} immediately after installing.");
            }

            // read pluginManifest from file again, since it might have superceded another and gotten the "Restart" memory-only status
            return new InstalledPluginInfo(pluginManifest, pluginIOHelper.ReadLoaderManifest(installDir), version.VersionNumber, installDir);
        }

        private IEnumerable<InternalDependencyInfo> AddInstalledPlugin(InstalledPluginInfo info)
        {
            _installedPlugins.GetOrAdd(info.Manifest.Id, []).Add(info);

            // don't resolve or install dependencies for inactive plugins
            if (info.Manifest.Status != PluginStatus.Active) yield break;

            // no loader manifest, so no need to populate dependencies
            if (info.LoaderManifest == null) yield break;

            if (info.LoaderManifest.Dependencies.Any(d => d.Versions.Count > 1))
            {
                throw new DependencyResolverException("Currently, only a single version per dependency is supported.");
            }

            foreach (var dependency in info.LoaderManifest.Dependencies)
            {
                var dependencyInfo = new InternalDependencyInfo(dependency.Manifest, dependency.ID, dependency.Versions, info.Manifest.Id);
                yield return dependencyInfo;
                _allDependencies.GetOrAdd(dependency.ID, []).Add(dependencyInfo);

            }
        }

        internal void ChangePluginState(string pluginPath, PluginManifest pluginManifest, PluginStatus state)
        {
            ArgumentException.ThrowIfNullOrEmpty(pluginPath);

            if (pluginManifest.Status == state)
            {
                // No need to save as the state hasn't changed.
                return;
            }

            pluginManifest.Status = state;
            pluginIOHelper.SavePluginManifest(pluginManifest, pluginPath);
        }

        internal void ChangePluginState(InstalledPluginInfo plugin, PluginStatus state)
        {
            ChangePluginState(plugin.Path, plugin.Manifest, state);
        }
    }
}
