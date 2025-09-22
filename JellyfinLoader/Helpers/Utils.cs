using CommandLine;
using Emby.Server.Implementations;
using Emby.Server.Implementations.AppBase;
using Emby.Server.Implementations.Plugins;
using Emby.Server.Implementations.Serialization;
using Jellyfin.Extensions.Json;
using Jellyfin.Extensions.Json.Converters;
using Jellyfin.Server;
using Jellyfin.Server.Helpers;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging;
using Serilog.Extensions.Logging;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace JellyfinLoader.Helpers
{
    internal class Utils : IHttpClientFactory
    {
        internal const int MainDepPoolID = -1;
        internal readonly Version MinimumVersion = new Version(0, 0, 0, 1);
        internal readonly Version AppVersion = typeof(ApplicationHost).Assembly.GetName().Version!;
        internal readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonDefaults.Options)
        {
            WriteIndented = true
        };

        // Jellyfin.Server.Startup
        internal HttpClient HttpClient = new(new SocketsHttpHandler()
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            AutomaticDecompression = DecompressionMethods.All,
            RequestHeaderEncodingSelector = (_, _) => Encoding.UTF8,
        });

        internal ILogger Logger;
        internal PluginManager PluginManager;
        internal string PluginsPath;
        internal string PluginConfigurationsPath;

        internal Utils()
        {
            var loggerFactory = ((SerilogLoggerFactory)typeof(Program).GetField("_loggerFactory", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!);
            Logger = loggerFactory.CreateLogger("JellyfinLoader");

            var applicationPaths = StartupHelpers.CreateApplicationPaths(Parser.Default.ParseArguments<StartupOptions>(Environment.GetCommandLineArgs()).Value);
            PluginsPath = applicationPaths.PluginsPath;
            PluginConfigurationsPath = applicationPaths.PluginConfigurationsPath;

            var pluginManager = new PluginManager(loggerFactory.CreateLogger<PluginManager>(), null!, (ServerConfiguration)ConfigurationHelper.GetXmlConfiguration(typeof(ServerConfiguration), applicationPaths.SystemConfigurationFilePath, new MyXmlSerializer()), "NON_EXISTENT_DIRECTORY_d524071f-b95c-452d-825e-c772f68b5957", AppVersion);
            typeof(PluginManager).GetField("_httpClientFactory", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(pluginManager, this);

            PluginManager = pluginManager;
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

        public HttpClient CreateClient(string name)
        {
            return HttpClient;
        }
    }
}
