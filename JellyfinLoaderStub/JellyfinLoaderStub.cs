using dnlib.DotNet;
using dnlib.DotNet.Emit;
using Emby.Server.Implementations.AppBase;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using System.Reflection;
using System.Runtime.Loader;

namespace JellyfinLoaderStub
{
    public class JellyfinLoaderStub : BasePlugin<JellyfinLoaderStubConfiguration>
    {
        public override string Name => "JellyfinLoader";
        public override Guid Id => Guid.Parse("d524071f-b95c-452d-825e-c772f68b5957");

        public JellyfinLoaderStub(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ISystemManager systemManager, IServerApplicationHost appHost, ILogger<JellyfinLoaderStub> logger) : base(applicationPaths, xmlSerializer) {
            var executingAssembly = Assembly.GetExecutingAssembly();
            var myDir = Path.GetDirectoryName(executingAssembly.Location)!;
            var mainAssemblyPath = Path.Combine(myDir, "JellyfinLoader.dll");

            if (AssemblyLoadContext.GetLoadContext(Assembly.GetEntryAssembly()).Assemblies.Any(assembly => assembly.Location == mainAssemblyPath))
            {
                // main dll already loaded, nothing to do
                return;
            }

            // we have been loaded through the normal jellyfin plugin load process (so not in main context), we want to patch and shutdown
            // first, manually load dnlib into our own assembly load context
            var alc = AssemblyLoadContext.GetLoadContext(executingAssembly)!;

            Assembly dnlibAssembly = alc.LoadFromAssemblyPath(Path.Combine(myDir, "dnlib.dll"));
            dnlibAssembly.GetTypes();

            // then perform patching
            PatchAndShutdown(systemManager, appHost, logger);
        }

        private static IMethod GetMethod(Importer importer, Type type, string method, Type[] types)
        {
            var methodBase = type.GetRuntimeMethod(method, types);
            return importer.Import(methodBase);
        }

        private static void PatchAndShutdown(ISystemManager systemManager, IServerApplicationHost appHost, ILogger<JellyfinLoaderStub> logger)
        {
            logger.LogInformation("Main DLL wasn't early loaded, patching Emby.Server.Implementations.dll...");

            var entryAssembly = Assembly.GetEntryAssembly()!;
            var rootDir = Path.GetDirectoryName(entryAssembly.Location)!;

            ModuleContext modCtx = ModuleDef.CreateModuleContext();
            var impDllPath = Path.Combine(rootDir, "Emby.Server.Implementations.dll");
            ModuleDefMD module = ModuleDefMD.Load(impDllPath, modCtx);
            var importer = new Importer(module);
            var serverApplicationPaths = module.Find("Emby.Server.Implementations.ServerApplicationPaths", false)!;

            var ctor = serverApplicationPaths.Methods.First(m => m.IsInstanceConstructor);

            // remove old return
            ctor.Body.Instructions.RemoveAt(ctor.Body.Instructions.Count - 1);

            // get main (entry assembly) load context
            ctor.Body.Instructions.Add(OpCodes.Call.ToInstruction(GetMethod(importer, typeof(Assembly), "GetEntryAssembly", [])));
            ctor.Body.Instructions.Add(OpCodes.Call.ToInstruction(GetMethod(importer, typeof(AssemblyLoadContext), "GetLoadContext", [typeof(Assembly)])));

            // load dnlib manually
            //ctor.Body.Instructions.Add(OpCodes.Dup.ToInstruction());
            //ctor.Body.Instructions.Add(OpCodes.Ldarg_0.ToInstruction());
            //ctor.Body.Instructions.Add(OpCodes.Call.ToInstruction(GetMethod(importer, typeof(BaseApplicationPaths), "get_PluginsPath", [])));
            //ctor.Body.Instructions.Add(OpCodes.Ldstr.ToInstruction("JellyfinLoader"));
            //ctor.Body.Instructions.Add(OpCodes.Ldstr.ToInstruction("dnlib.dll"));
            //ctor.Body.Instructions.Add(OpCodes.Call.ToInstruction(GetMethod(importer, typeof(Path), "Combine", [typeof(string), typeof(string), typeof(string)])));
            //ctor.Body.Instructions.Add(OpCodes.Callvirt.ToInstruction(GetMethod(importer, typeof(AssemblyLoadContext), "LoadFromAssemblyPath", [typeof(string)])));
            //ctor.Body.Instructions.Add(OpCodes.Pop.ToInstruction());

            // load harmony manually
            //ctor.Body.Instructions.Add(OpCodes.Dup.ToInstruction());
            //ctor.Body.Instructions.Add(OpCodes.Ldarg_0.ToInstruction());
            //ctor.Body.Instructions.Add(OpCodes.Call.ToInstruction(GetMethod(importer, typeof(BaseApplicationPaths), "get_PluginsPath", [])));
            //ctor.Body.Instructions.Add(OpCodes.Ldstr.ToInstruction("JellyfinLoader"));
            //ctor.Body.Instructions.Add(OpCodes.Ldstr.ToInstruction("0Harmony.dll"));
            //ctor.Body.Instructions.Add(OpCodes.Call.ToInstruction(GetMethod(importer, typeof(Path), "Combine", [typeof(string), typeof(string), typeof(string)])));
            //ctor.Body.Instructions.Add(OpCodes.Callvirt.ToInstruction(GetMethod(importer, typeof(AssemblyLoadContext), "LoadFromAssemblyPath", [typeof(string)])));
            //ctor.Body.Instructions.Add(OpCodes.Pop.ToInstruction());

            // TODO: gracefully handle absence of jellyfinLoader plugin

            // load main dll into main load context
            ctor.Body.Instructions.Add(OpCodes.Ldarg_0.ToInstruction());
            ctor.Body.Instructions.Add(OpCodes.Call.ToInstruction(GetMethod(importer, typeof(BaseApplicationPaths), "get_PluginsPath", [])));
            ctor.Body.Instructions.Add(OpCodes.Ldstr.ToInstruction("JellyfinLoader"));
            ctor.Body.Instructions.Add(OpCodes.Ldstr.ToInstruction("JellyfinLoader.dll"));
            ctor.Body.Instructions.Add(OpCodes.Call.ToInstruction(GetMethod(importer, typeof(Path), "Combine", [typeof(string), typeof(string), typeof(string)])));
            ctor.Body.Instructions.Add(OpCodes.Callvirt.ToInstruction(GetMethod(importer, typeof(AssemblyLoadContext), "LoadFromAssemblyPath", [typeof(string)])));

            // invoke Bootstrap function
            ctor.Body.Instructions.Add(OpCodes.Ldstr.ToInstruction("JellyfinLoader.JellyfinLoader"));
            ctor.Body.Instructions.Add(OpCodes.Callvirt.ToInstruction(GetMethod(importer, typeof(Assembly), "GetType", [typeof(string)])));
            ctor.Body.Instructions.Add(OpCodes.Ldstr.ToInstruction("Bootstrap"));
            ctor.Body.Instructions.Add(OpCodes.Callvirt.ToInstruction(GetMethod(importer, typeof(Type), "GetMethod", [typeof(string)])));
            ctor.Body.Instructions.Add(OpCodes.Ldnull.ToInstruction());
            ctor.Body.Instructions.Add(OpCodes.Ldnull.ToInstruction());
            ctor.Body.Instructions.Add(OpCodes.Callvirt.ToInstruction(GetMethod(importer, typeof(MethodBase), "Invoke", [typeof(object), typeof(object[])])));
            ctor.Body.Instructions.Add(OpCodes.Pop.ToInstruction());
            ctor.Body.Instructions.Add(OpCodes.Ret.ToInstruction());

            // shouldn't be needed, but just to be sure
            ctor.Body.UpdateInstructionOffsets();

            File.Move(impDllPath, Path.Combine(rootDir, "Emby.Server.Implementations.dll.bak"));

            module.Write(Path.Combine(rootDir, "Emby.Server.Implementations.dll"));

            Task.Run(async () =>
            {
                // allow full startup so as to not break things
                while (!appHost.CoreStartupHasCompleted)
                {
                    await Task.Delay(100);
                }

                var myDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
                var alc = AssemblyLoadContext.GetLoadContext(entryAssembly)!;

                Assembly mainAssembly = alc.LoadFromAssemblyPath(Path.Combine(myDir, "JellyfinLoader.dll"));
                Type t = mainAssembly.GetType("JellyfinLoader.JellyfinLoader")!;
                t.GetMethod("Bootstrap")!.Invoke(null, null);

                logger.LogInformation("One-time setup completed. Restarting...");

                systemManager.Restart();
            });
        }
    }
}
