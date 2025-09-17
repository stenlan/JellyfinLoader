using dnlib.DotNet;
using dnlib.DotNet.Emit;
using Emby.Server.Implementations.AppBase;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;

namespace JLTrampoline
{
    public class JLTrampoline : BasePlugin<JLTrampolineConfiguration>
    {
        internal const string PluginId = "d524071f-b95c-452d-825e-c772f68b5957";
        internal const string MainAssemblyName = "JellyfinLoader.dll";
        internal const string MainFullType = "JellyfinLoader.JellyfinLoader";
        private const string DiskPatchAssemblyBackupName = "JellyfinLoaderBackup_DoNotDelete.dat";

        public override string Name => "JellyfinLoader";
        public override Guid Id => Guid.Parse(PluginId);


        public JLTrampoline(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ISystemManager systemManager, IServerApplicationHost appHost, ILogger<JLTrampoline> logger) : base(applicationPaths, xmlSerializer) {
            var executingAssembly = Assembly.GetExecutingAssembly();
            var myDir = Path.GetDirectoryName(executingAssembly.Location)!;
            var mainAssemblyPath = Path.Combine(myDir, MainAssemblyName);

            var entryAssembly = Assembly.GetEntryAssembly()!;
            var rootDir = Path.GetDirectoryName(entryAssembly.Location)!;
            var patchDllTargetPath = Path.Combine(rootDir, "Emby.Server.Implementations.dll");

            // cleanup old file
            if (File.Exists(patchDllTargetPath + ".old")) File.Delete(patchDllTargetPath + ".old");

            if (AssemblyLoadContext.GetLoadContext(Assembly.GetEntryAssembly()).Assemblies.Any(assembly => assembly.Location == mainAssemblyPath))
            {
                // main dll already loaded, nothing to do
                return;
            }

            // we have been loaded through the normal jellyfin plugin load process (so not in main context), we want to patch and restart.
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
            logger.LogInformation("Main DLL wasn't early loaded, patching Jellyfin...");

            var entryAssembly = Assembly.GetEntryAssembly()!;
            var rootDir = Path.GetDirectoryName(entryAssembly.Location)!;

            ModuleContext modCtx = ModuleDef.CreateModuleContext();

            // TODO: sig checks?
            var backupDllPath = Path.Combine(rootDir, DiskPatchAssemblyBackupName);
            var backupExists = File.Exists(backupDllPath);
            var patchDllTargetPath = Path.Combine(rootDir, "Emby.Server.Implementations.dll");

            if (backupExists && FileVersionInfo.GetVersionInfo(backupDllPath).FileVersion != FileVersionInfo.GetVersionInfo(patchDllTargetPath).FileVersion)
            {
                // backup is of different version than current dll, assume unpatched
                // TODO: handle changes to patch, somehow detect patch version? maybe save to fileversioninfo.specialBuild or different property
                File.Delete(backupDllPath);
                backupExists = false;
            }

            var patchDllSourcePath = backupExists ? backupDllPath : patchDllTargetPath;

            ModuleDefMD module = ModuleDefMD.Load(patchDllSourcePath, modCtx);
            var importer = new Importer(module);

            ModuleDefMD thisModule = ModuleDefMD.Load(typeof(CILHolder).Module, modCtx);
            var testCIL = thisModule.Find(typeof(CILHolder).FullName, false)!;
            var tryLoadDLLMeth = testCIL.FindMethod(nameof(CILHolder.TryLoadDLL));

            var serverApplicationPaths = module.Find("Emby.Server.Implementations.ServerApplicationPaths", false)!;
            var loaderMethod = new MethodDefUser(tryLoadDLLMeth.Name, tryLoadDLLMeth.MethodSig, tryLoadDLLMeth.ImplAttributes, tryLoadDLLMeth.Attributes);
            serverApplicationPaths.Methods.Add(loaderMethod);
            loaderMethod.Body = new CilBody(tryLoadDLLMeth.Body.InitLocals, tryLoadDLLMeth.Body.Instructions, tryLoadDLLMeth.Body.ExceptionHandlers, tryLoadDLLMeth.Body.Variables);

            var ctor = serverApplicationPaths.Methods.First(m => m.IsInstanceConstructor);

            // remove old return
            ctor.Body.Instructions.RemoveAt(ctor.Body.Instructions.Count - 1);

            // call TryLoadDLL
            ctor.Body.Instructions.Add(OpCodes.Ldarg_0.ToInstruction());
            ctor.Body.Instructions.Add(OpCodes.Call.ToInstruction(GetMethod(importer, typeof(BaseApplicationPaths), "get_PluginsPath", [])));
            ctor.Body.Instructions.Add(OpCodes.Call.ToInstruction(serverApplicationPaths.FindMethod(nameof(CILHolder.TryLoadDLL))));

            // new return
            ctor.Body.Instructions.Add(OpCodes.Ret.ToInstruction());

            // shouldn't be needed, but just to be sure
            ctor.Body.UpdateInstructionOffsets();

            if (backupExists)
            {
                // don't touch existing backup
                File.Move(patchDllTargetPath, patchDllTargetPath + ".old");
            }
            else
            {
                File.Move(patchDllTargetPath, backupDllPath);
            }

            module.Write(patchDllTargetPath);

            Task.Run(async () =>
            {
                // allow full startup so as to not break things
                while (!appHost.CoreStartupHasCompleted)
                {
                    await Task.Delay(100);
                }

                var myDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
                var alc = AssemblyLoadContext.GetLoadContext(entryAssembly)!;

                Assembly mainAssembly = alc.LoadFromAssemblyPath(Path.Combine(myDir, MainAssemblyName));
                Type t = mainAssembly.GetType(MainFullType)!;
                t.GetMethod("Bootstrap")!.Invoke(null, null);

                logger.LogInformation("One-time setup completed. Restarting...");

                systemManager.Restart();
            });
        }
    }
}
