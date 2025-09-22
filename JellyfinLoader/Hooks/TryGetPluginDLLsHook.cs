using Emby.Server.Implementations.Plugins;
using HarmonyLib;
using MediaBrowser.Common.Plugins;
using Microsoft.Extensions.Logging;

namespace JellyfinLoader.Hooks
{
    [HarmonyPatch(typeof(PluginManager), "TryGetPluginDlls")]
    [HarmonyPatchCategory(nameof(TryGetPluginDLLsHook))]
    internal class TryGetPluginDLLsHook
    {
        private static bool Prefix(LocalPlugin plugin, ILogger<PluginManager> ____logger, ref IReadOnlyList<string> whitelistedDlls, ref bool __result)
        {
            __result = JellyfinLoader.Instance.pluginIOHelper.TryGetPluginDLLs(plugin, ref whitelistedDlls, ____logger);
            return false;
        }
    }
}
