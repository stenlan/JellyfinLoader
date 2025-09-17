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

namespace JLTrampoline
{
    public class JLTrampoline : BasePlugin<JLTrampolineConfiguration>
    {
        public override string Name => "JellyfinLoader";
        public override Guid Id => Guid.Parse("d524071f-b95c-452d-825e-c772f68b5957");

        public JLTrampoline(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ISystemManager systemManager, IServerApplicationHost appHost, ILogger<JLTrampoline> logger) : base(applicationPaths, xmlSerializer) {
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

        private static void PatchAndShutdown(ISystemManager systemManager, IServerApplicationHost appHost, ILogger<JLTrampoline> logger)
        {
            logger.LogInformation("Main DLL wasn't early loaded, patching Emby.Server.Implementations.dll...");

            var entryAssembly = Assembly.GetEntryAssembly()!;
            var rootDir = Path.GetDirectoryName(entryAssembly.Location)!;

            ModuleContext modCtx = ModuleDef.CreateModuleContext();
            var impDllPath = Path.Combine(rootDir, "Emby.Server.Implementations.dll");
            ModuleDefMD module = ModuleDefMD.Load(impDllPath, modCtx);
            ModuleDefMD thisModule = ModuleDefMD.Load(typeof(CILHolder).Module, modCtx);
            var importer = new Importer(module);
            var serverApplicationPaths = module.Find("Emby.Server.Implementations.ServerApplicationPaths", false)!;
            var testCIL = thisModule.Find("JellyfinLoaderStub.TestCIL", false)!;
            var tryLoadDLLMeth = testCIL.FindMethod(nameof(CILHolder.TryLoadDLL));
            var meth1 = new MethodDefUser(tryLoadDLLMeth.Name, tryLoadDLLMeth.MethodSig, tryLoadDLLMeth.ImplAttributes, tryLoadDLLMeth.Attributes);

            serverApplicationPaths.Methods.Add(meth1);
            meth1.Body = new CilBody(tryLoadDLLMeth.Body.InitLocals, tryLoadDLLMeth.Body.Instructions, tryLoadDLLMeth.Body.ExceptionHandlers, tryLoadDLLMeth.Body.Variables);

            var ctor = serverApplicationPaths.Methods.First(m => m.IsInstanceConstructor);

            // remove old return
            ctor.Body.Instructions.RemoveAt(ctor.Body.Instructions.Count - 1);

            // call TryLoadDLL
            ctor.Body.Instructions.Add(OpCodes.Ldarg_0.ToInstruction());
            ctor.Body.Instructions.Add(OpCodes.Call.ToInstruction(GetMethod(importer, typeof(BaseApplicationPaths), "get_PluginsPath", [])));
            ctor.Body.Instructions.Add(OpCodes.Call.ToInstruction(serverApplicationPaths.FindMethod(nameof(CILHolder.TryLoadDLL))));

            // new return
            ctor.Body.Instructions.Add(OpCodes.Ret.ToInstruction());

            // https://github.com/0xd4d/dnlib/blob/master/Examples/Example2.cs
            // https://pwn.report/2023/04/20/flareon9-writeups-p2.html
            // https://github.com/0xd4d/dnlib/blob/master/src/DotNet/Emit/MethodUtils.cs
            // GetMethodFlags(), FixCallTargets()

            //ctor.Body.

            //MethodBodyReader.CreateCilBody()
            //ctor.Body.Variables.Add(new Local())

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
