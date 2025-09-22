using HarmonyLib;
using Jellyfin.Server;
using System.Reflection;
namespace JellyfinLoader.Hooks
{
    [HarmonyPatch(typeof(Program), "StartServer")]
    [HarmonyPatchCategory(nameof(StartServerHook))]
    internal class StartServerHook
    {
        // All plugins are unloaded at this point. Harmony is available.
        private static bool Prefix(ref Task __result)
        {
            if (!JellyfinLoader.Instance.StartServer())
            {
                typeof(Program).GetField("_restartOnShutdown", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, false);
                __result = Task.CompletedTask;
                return false;
            }

            return true;
        }
    }
}