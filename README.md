# JellyfinLoader

A Jellyfin plugin that modifies Jellyfin's plugin load logic (by patching functions both in-memory and on disk) to give plugins more control.

## Why?
Some plugins want to do things that aren't easily done by a normal Jellyfin plugin. For example:
- Being used as a library (without JellyfinLoader, that requires [hacky workarounds](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation?tab=readme-ov-file#referencing-this-as-a-library))
- Using other plugins as a library.
- Injecting super early, before other plugins or before server start.
- Being loaded into the main assembly load context.

## How does it work?
// TODO. In short, it patches a DLL on disk that loads a different DLL very early in the Jellyfin startup process, then performs the necessary instrumentation.

## How do I use this?
**If you are a Jellyfin user** and you are trying to install a plugin that depends on JellyfinLoader, you won't need to do anything. JellyfinLoad should automatically take care of installing and updating itself. The repository URL is [https://raw.githubusercontent.com/stenlan/JellyfinLoader/refs/heads/main/repository.json](https://raw.githubusercontent.com/stenlan/JellyfinLoader/refs/heads/main/repository.json).

**If you are a Jellyfin plugin developer**, read along.

### Setup
- Install [the JellyfinLoaderStub NuGet package](https://www.nuget.org/packages/JellyfinLoaderStub/) or simply download its latest release from [the GitHub releases page](https://github.com/stenlan/JellyfinLoader/releases/latest) and include it in your project.
- Change the `assemblies` entry in your plugin's `meta.json` to _just_ `["JellyfinLoaderStub.dll"]`. That's right, an array with a single element, pointing to the stub. If you relied on this value before, you can put it in the `loader.json` file instead (see next step).
- Add a `loader.json` file to your plugin's directory. See [The loader.json file](#the-loaderjson-file) for more information.

That's it! Make sure to include the `JellyfinLoaderStub.dll` in the same folder as your plugin when installing/using it on your Jellyfin server. The stub will take care of everything, including automatic installation of JellyfinLoader if it is missing, and automatic resolving and installation of any dependencies you specify in the `loader.json` file.

### The loader.json file
The `loader.json` file with all its properties set to the default values looks like this:
```jsonc
{
  "loadContext": "Plugin",
  "loadTiming": "Default",
  "assemblies": [],
  "dependencies": []
}
```

#### loadContext
The AssemblyLoadContext of the plugin, either `Plugin` or `Main` (default `Plugin`). Note that you almost always want to use `Plugin`, even as a library plugin/when you expect other plugins to depend on your plugin. The dependency resolver will take care of load order.

It is an error for a plugin with its loadContext set to `Main` to depend on a plugin that has its loadContext set to `Plugin` (or a non-JellyfinLoader plugin).

In case a plugin is loaded into the main load context, it is not unloaded between server soft restarts, and there are some other limitations and pitfalls.  
TODO: document limitations and pitfalls

You can also read the code comments in the `loader.json` model file in [LoaderPluginManifest.cs](./JellyfinLoader/Models/LoaderPluginManifest.cs).

#### loadTiming
The moment at which you want the plugin's assemblies to be loaded, either `Default` or `Early`. This is entirely separate from the `loadContext`, and all combinations of `loadContext`s and `loadTiming`s are possible.
 
Plugins are never loaded earlier than their dependencies, so if any of your dependencies have a `Default` load timing, then your plugin will also have a `Default` load timing, even if you specify `Early` here. A `Default` load timing will always be respected, even if your dependencies have an `Early` load timing. JellyfinLoader will warn you when this value is not respected.

Plugins that have an `Early` load timing can use the [JellyfinLoader.IEarlyLoadPlugin interface](./JellyfinLoaderStub/IEarlyLoadPlugin.cs).

You can implement `IEarlyLoadPlugin`'s `OnServerStart(bool coldStart)` function, which will run first thing on every call to [`Program#StartServer`](https://github.com/jellyfin/jellyfin/blob/db2dbaa62b85ba59ad2cfdcb99da71beb10cfe94/Jellyfin.Server/Program.cs#L157). The `coldStart` parameter denotes if this is the first time your `StartServer` hook was called during this run of the Jellyfin server executable, or a subsequent one. When the Jellyfin server is fully shutdown and restarted, `coldStart` will be true again. However, if the Jellyfin server is simply restarted through the web interface (or the underlying API endpoint), `coldStart` will be false. This can be useful for one-time initialization logic upon server start, like [applying Harmony patches](#using-harmony).

It is strongly discouraged to add a constructor to any classes implementing the `IEarlyLoadPlugin` interface. If you do, that constructor will run _very_ early ([just after `_loggerFactory.CreateLogger("Main")` in `Program#StartApp`](https://github.com/jellyfin/jellyfin/blob/db2dbaa62b85ba59ad2cfdcb99da71beb10cfe94/Jellyfin.Server/Program.cs#L89)) and JellyfinLoader will make no stability guarantees whatsoever. For nearly any use case, the `OnServerStart` should be more than early enough.

#### dependencies
Dependencies your plugin relies on. See the example value:

```jsonc
  "dependencies": [
    {
      "manifest": "https://raw.githubusercontent.com/streamyfin/jellyfin-plugin-streamyfin/refs/heads/main/manifest.json", // repository json of the dependency plugin
      "id": "1e9e5d38-6e67-4615-8719-e98a5c34f004", // GUID of the dependency plugin
      "version": ["0.62.0.0"] // list of supported versions. Currently, only a single version per dependency is supported, but SemVer-like syntax is planned for the future.
    }
  ]
```

JellyfinLoader will automatically take care of installing dependencies and resolving dependency trees, as well as making sure dependent plugins are loaded into the same assembly load context as their dependencies.

#### assemblies
The assembly whitelist - this replaces the role of the `assemblies` field in `meta.json`, since - if you followed the instructions - that field should be set to `["JellyfinLoaderStub.dll"]`. Like the `meta.json` file, this value can be omitted and will default to no whitelist (`[]`) in which case all the DLL files found in your plugin's folder will be loaded, which suffices for almost all use cases.

### Using Harmony
To use **Harmony**, simply link against **Lib.Harmony 2.4.1.0** and use Harmony like normal. JellyfinLoader will take care of the DLL being loaded.

## Limitations
This plugin is still in its early stages, so expect bugs. If you have any questions, don't hesitate to [create an issue](https://github.com/stenlan/JellyfinLoader/issues/new).

Currently, only Jellyfin `10.10.7.0` is supported. `10.11.0.0` support is planned, as well as more thorough documentation
