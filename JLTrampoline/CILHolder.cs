using Jellyfin.Data.Entities.Libraries;
using Jellyfin.Extensions.Json;
using Jellyfin.Extensions.Json.Converters;
using MediaBrowser.Common.Plugins;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;

namespace JLTrampoline
{
    /// <summary>
    /// Class used for easy CIL generation when patching
    /// </summary>
    internal class CILHolder
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void TryLoadDLL(string PluginsPath)
        {
            var metaPaths = Directory.EnumerateFiles(PluginsPath, "meta.json", SearchOption.AllDirectories);
            string? maxMetaPath = null;
            Version? maxVersion = null;
            var _jsonOptions = new JsonSerializerOptions(JsonDefaults.Options)
            {
                WriteIndented = true
            };

            // We need to use the default GUID converter, so we need to remove any custom ones.
            for (int a = _jsonOptions.Converters.Count - 1; a >= 0; a--)
            {
                if (_jsonOptions.Converters[a] is JsonGuidConverter convertor)
                {
                    _jsonOptions.Converters.Remove(convertor);
                    break;
                }
            }

            foreach (var metaPath in metaPaths)
            {
                try
                {
                    var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllBytes(metaPath), _jsonOptions);
                    if (manifest.Id.ToString() != "d524071f-b95c-452d-825e-c772f68b5957") continue;

                    var version = Version.Parse(manifest.Version);
                    if (maxMetaPath == null || version > maxVersion)
                    {
                        maxVersion = version;
                        maxMetaPath = metaPath;
                    }
                }
                catch
                {
                    continue;
                }
            }

            if (maxMetaPath != null)
            {
                AssemblyLoadContext.GetLoadContext(Assembly.GetEntryAssembly()).LoadFromAssemblyPath(Path.Combine(Path.GetDirectoryName(maxMetaPath), "JellyfinLoader.dll")).GetType("JellyfinLoader.JellyfinLoader").GetMethod("Bootstrap").Invoke(null, null);
            }
        }
    }
}
