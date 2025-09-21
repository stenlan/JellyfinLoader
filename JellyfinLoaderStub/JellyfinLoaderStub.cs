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
            if (plugins.Any(plugin => 
                plugin.Id == JellyfinLoaderGuid &&
                plugin.Manifest.Status != PluginStatus.Superceded &&
                plugin.Manifest.Status != PluginStatus.NotSupported)
            ) return;

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
