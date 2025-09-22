using Emby.Server.Implementations.Plugins;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Common.Updates;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Updates;
using Microsoft.Extensions.Logging;
using System.Reflection;

namespace JellyfinLoaderStub
{
    // makes use of the fact that IMetadataProvider is constructed by the application host in ApplicationHost#FindParts just after all plugins have finished initializing,
    // and is also just a marker interface (doesn't implement the generic variant), so there should be no runtime impact
    public class JellyfinLoaderStub : IMetadataProvider
    {
        private static readonly Guid JellyfinLoaderGuid = Guid.Parse("d524071f-b95c-452d-825e-c772f68b5957");
        private const string RepositoryURL = "https://raw.githubusercontent.com/stenlan/JellyfinLoader/refs/heads/main/repository.json";

        public JellyfinLoaderStub(IServerApplicationHost applicationHost, ISystemManager systemManager, IPluginManager iPluginManager, IServerConfigurationManager serverConfigurationManager, IInstallationManager installationManager, ILogger<JellyfinLoaderStub> logger)
        {
            // try and see if we are the first stub to be loaded, to not have multiple instances run/try to install anything at the same time
            foreach (var type in applicationHost.GetExportTypes<IMetadataProvider>())
            {
                // an actual metadata provider, not a loader stub
                if (type.Name != GetType().Name) continue;

                // we are the first
                if (type.Assembly.Location == GetType().Assembly.Location) break;

                // else, we return
                return;
            }

            if (iPluginManager is not PluginManager pluginManager) throw new InvalidOperationException("JellyfinLoaderStub was provided an invalid IPluginManager instance.");
            var plugins = (List<LocalPlugin>)typeof(PluginManager).GetField("_plugins", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pluginManager)!;

            // if a compatible jellyfinLoader is already installed, we're done here.
            // even if it has been disabled, deleted, needs a restart, etc, because we don't want to strongarm users trying to
            // remove or disable jellyfinLoader
            var jlPlugins = plugins.Where(plugin => plugin.Id == JellyfinLoaderGuid);

            // A version of JL is installed
            if (jlPlugins.Any())
            {
                // A version of JL is installed, but none are active for some reason. Its state can be either:
                // - Restart, but this is a pretty much impossible state for the stub to end up encountering.
                // - Disabled, in which case this was an explicit action by the user and we don't want to override them.
                // - Not supported, in which case updating should be handled through JL's update mechanism.
                // - Malfunctioned, in which case something else is wrong, but it's not the stub's responsibility.
                // - Superceded, in which case a newer instance of JL probably exists but if not, it is in one of the states above or simply deleted.
                // - Deleted, either as the plugin status or actually deleted from disk.

                if (!jlPlugins.Any(plugin => plugin.Manifest.Status == PluginStatus.Active))
                {
                    foreach (var jlPlugin in jlPlugins)
                    {
                        logger.LogWarning("An instance of JellyfinLoader was found at {pluginPath}, but its status is \"{pluginStatus}\". JellyfinLoader plugins will not load.", jlPlugin.Path, jlPlugin.Manifest.Status);
                    }
                }

                return;
            }

            var jellyfinLoaderRepo = serverConfigurationManager.Configuration.PluginRepositories.FirstOrDefault(repo => repo.Url == RepositoryURL);

            if (jellyfinLoaderRepo is null)
            {
                jellyfinLoaderRepo = new RepositoryInfo()
                {
                    Enabled = true,
                    Name = "JellyfinLoader",
                    Url = RepositoryURL
                };
                serverConfigurationManager.Configuration.PluginRepositories = [..serverConfigurationManager.Configuration.PluginRepositories, jellyfinLoaderRepo];
                serverConfigurationManager.SaveConfiguration();
            }

            _ = InstallJellyfinLoader(applicationHost, systemManager, installationManager, jellyfinLoaderRepo, logger);
        }

        private async Task InstallJellyfinLoader(IServerApplicationHost applicationHost, ISystemManager systemManager, IInstallationManager installationManager, RepositoryInfo repositoryInfo, ILogger<JellyfinLoaderStub> logger)
        {
            ArgumentNullException.ThrowIfNull(repositoryInfo.Name);
            ArgumentNullException.ThrowIfNull(repositoryInfo.Url);
            logger.LogInformation("JellyfinLoader not installed, installing...");
            var jellyfinLoaderPackage = (await installationManager.GetPackages(repositoryInfo.Name, repositoryInfo.Url, true)).First(package => package.Id == JellyfinLoaderGuid);
            var latestCompatibleVersion = jellyfinLoaderPackage.Versions.Where(version => Version.Parse(version.TargetAbi!) == applicationHost.ApplicationVersion).MaxBy(versionInfo => versionInfo.VersionNumber);

            if (latestCompatibleVersion == null)
            {
                logger.LogError("Could not find a compatible JellyfinLoader version!");
                throw new ArgumentException("[JellyfinLoaderStub] Could not find a compatible JellyfinLoader version!");
            }

            await installationManager.InstallPackage(new InstallationInfo
            {
                Changelog = latestCompatibleVersion.Changelog,
                Id = jellyfinLoaderPackage.Id,
                Name = jellyfinLoaderPackage.Name,
                Version = latestCompatibleVersion.VersionNumber,
                SourceUrl = latestCompatibleVersion.SourceUrl,
                Checksum = latestCompatibleVersion.Checksum,
                PackageInfo = jellyfinLoaderPackage
            });

            logger.LogInformation("JellyfinLoader has been installed. Waiting for full startup before restarting...");

            // allow full startup so as to not break things
            while (!applicationHost.CoreStartupHasCompleted)
            {
                await Task.Delay(100);
            }

            systemManager.Restart();
        }

        // IMetadataProvider method that should never be called
        public string Name => throw new NotImplementedException("Should never be called");
    }
}
