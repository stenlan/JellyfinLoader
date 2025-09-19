using MediaBrowser.Model.Plugins;

namespace JellyfinLoader
{
    internal record InstalledPluginInfo(Guid ID, Version Version, PluginStatus Status, string Name, string Path);
}
