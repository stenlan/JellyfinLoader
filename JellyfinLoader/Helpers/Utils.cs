using Emby.Server.Implementations;
using Emby.Server.Implementations.Plugins;
using Jellyfin.Extensions.Json;
using Jellyfin.Extensions.Json.Converters;
using MediaBrowser.Common.Plugins;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;

namespace JellyfinLoader.Helpers
{
    internal static class Utils
    {
        internal static readonly Version AppVersion = typeof(ApplicationHost).Assembly.GetName().Version!;
        internal static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonDefaults.Options)
        {
            WriteIndented = true
        };

        // Jellyfin.Server.Startup
        internal static HttpClient HttpClient = new(new SocketsHttpHandler()
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            AutomaticDecompression = DecompressionMethods.All,
            RequestHeaderEncodingSelector = (_, _) => Encoding.UTF8,
        });

        internal static ILogger Logger;

        internal static PluginManager PluginManager;

        internal static string PluginsPath;

        internal static readonly Version MinimumVersion = new Version(0, 0, 0, 1);

        static Utils()
        {
            for (int a = JsonOptions.Converters.Count - 1; a >= 0; a--)
            {
                if (JsonOptions.Converters[a] is JsonGuidConverter convertor)
                {
                    JsonOptions.Converters.Remove(convertor);
                    break;
                }
            }

            HttpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(
                "Jellyfin-Server",
                AppVersion.ToString()
            ));
            // Jellyfin.Server.Startup
            HttpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json, 1.0));
            HttpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Xml, 0.9));
            HttpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.8));
        }

        public static TValue GetOrAdd<TKey, TValue>(this IDictionary<TKey, TValue> dict, TKey key, TValue value)
        {
            var hasEntry = dict.TryGetValue(key, out var outVar);
            if (!hasEntry)
            {
                dict[key] = value;
                return value;
            }
            return outVar!;
        }
    }
}
