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
            return JellyfinLoader.Instance.assemblyLoader.LoadFromAssemblyPath(assemblyPath, ref __result);
        }
    }
}
