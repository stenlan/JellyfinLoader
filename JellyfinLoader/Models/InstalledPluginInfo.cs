using MediaBrowser.Model.Plugins;

namespace JellyfinLoader.Models
{
    internal record InstalledPluginInfo(Guid ID, Version Version, PluginStatus Status, string Name, string Path);
}
