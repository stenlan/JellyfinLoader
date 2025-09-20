using MediaBrowser.Common.Plugins;

namespace JellyfinLoader.Models
{
    internal record InstalledPluginInfo(PluginManifest Manifest, LoaderPluginManifest? LoaderManifest, Version Version, string Path)
    {
        public virtual bool Equals(InstalledPluginInfo? other)
        {
            return other is not null && Path == other.Path;
        }

        public override int GetHashCode()
        {
            return Path.GetHashCode();
        }
    }
}
