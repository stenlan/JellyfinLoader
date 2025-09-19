using MediaBrowser.Common.Plugins;

namespace JellyfinLoader.Models
{
    internal record PackageInstallationResult(List<DependencyInfo> NewDependencies, string InstallDir, LocalPlugin LocalPlugin);
}
