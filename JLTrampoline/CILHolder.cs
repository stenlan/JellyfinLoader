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
    /// Class used for easy CIL generation when patching.
    /// Note: We CAN NOT reference members in the JLTrampoline namespace here, except for compile-time constants because they are directly inserted into the CIL as literals.
    /// </summary>
    internal class CILHolder
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void TryLoadDLL()
        {
            var myAssembly = Assembly.GetExecutingAssembly();
            var myDir = Path.GetDirectoryName(myAssembly.Location)!;
            var pluginsDir = Directory.GetParent(myDir)!.FullName;
            var metaPaths = Directory.EnumerateFiles(pluginsDir, "meta.json", SearchOption.AllDirectories);
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
                    if (manifest.Id.ToString() != JLTrampoline.PluginId) continue;

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
                AssemblyLoadContext.GetLoadContext(Assembly.GetEntryAssembly()).LoadFromAssemblyPath(Path.Combine(Path.GetDirectoryName(maxMetaPath), JLTrampoline.MainAssemblyName)).GetType(JLTrampoline.MainFullType).GetMethod("Bootstrap").Invoke(null, null);
            }
        }
    }
}
