using Emby.Server.Implementations.Plugins;
using HarmonyLib;
using System.Reflection;
using System.Runtime.Loader;

namespace JellyfinLoader.Hooks
{
    [HarmonyPatch(typeof(AssemblyLoadContext), "LoadFromAssemblyPath")]
    [HarmonyPatchCategory(nameof(LoadFromAssemblyPathHook))]
    internal static class LoadFromAssemblyPathHook
    {
        static bool Prefix(AssemblyLoadContext __instance, string assemblyPath, ref Assembly __result)
        {
            if (__instance is not PluginLoadContext instance) return true;

            var ourRes = JellyfinLoader.Instance.LoadFromAssemblyPath(assemblyPath);

            if (ourRes != null)
            {
                __result = ourRes;
                return false;
            }

            return true;
        }
    }
}
