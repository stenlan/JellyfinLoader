# JellyfinLoader

A Jellyfin plugin that modifies Jellyfin's plugin load logic (by patching functions both in-memory and on disk) to give plugins more control.

## Why?
Some plugins want to do things that aren't easily done by a normal Jellyfin plugin. For example:
- Being used as a library (without JellyfinLoader, that requires [hacky workarounds](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation?tab=readme-ov-file#referencing-this-as-a-library))
- Injecting super early, before other plugins or before server start.
- Being loaded into the main assembly load context (to use things like [Harmony](https://harmony.pardeike.net/) for example).

## How does it work?
// TODO. In short, it patches a DLL on disk that loads a different DLL very early in the Jellyfin startup process, then performs the necessary instrumentation.

## How do I use this??
**If you are a Jellyfin user** and you are trying to install a plugin that depends on JellyfinLoader, simply install JellyfinLoader alongside that plugin. The repository URL is [https://raw.githubusercontent.com/stenlan/JellyfinLoader/refs/heads/main/repository.json](https://raw.githubusercontent.com/stenlan/JellyfinLoader/refs/heads/main/repository.json).

**If you are a Jellyfin plugin developer**, read along.

### Using Harmony
To use **Harmony**, simply link against **Lib.Harmony 2.4.1.0** and use Harmony like normal. JellyfinLoader will take care of the DLL being loaded.

### Early loading and library plugins
To inject early and/or be loaded into the main assembly context instead of your separate plugin context (useful for library plugins), simply add a `loader.json` alongside your plugin's `meta.json`, with at least the following content:

```json
{
  "loadControl": "EARLY"
}
```

This will load your plugin into the main assembly context, and way before normal plugins are loaded. Then to actually do anything useful with the early loading, you probably want to implement the [JelyfinLoader.IEarlyLoadPlugin interface](https://github.com/stenlan/JellyfinLoader/blob/main/JellyfinLoader/IEarlyLoadPlugin.cs).

If you add a zero-parameter constructor to a class implementing the `IEarlyLoadPlugin` interface, that constructor will run very early ([just after `StartupHelpers#CreateApplicationPaths` in `Program#StartApp`](https://github.com/jellyfin/jellyfin/blob/db2dbaa62b85ba59ad2cfdcb99da71beb10cfe94/Jellyfin.Server/Program.cs#L89)).

Additionally, you can implement `IEarlyLoadPlugin`'s `OnServerStart(bool coldStart)` function, which will run first thing on every call to [`Program#StartServer`](https://github.com/jellyfin/jellyfin/blob/db2dbaa62b85ba59ad2cfdcb99da71beb10cfe94/Jellyfin.Server/Program.cs#L157). The `coldStart` parameter denotes if this is the first time your `StartServer` hook was called during this run of the Jellyfin server executable, or a subsequent one. When the Jellyfin server is fully shutdown and restarted, `coldStart` will be true again. However, if the Jellyfin server is simply restarted through the web interface (or the underlying API endpoint), `coldStart` will be false. This can be useful for one-time initialization logic upon server start, like applying Harmony patches.

## Limitations
This plugin is still in its early stages, so expect bugs.

Currently, Jellyfin breaks if JellyfinLoader is uninstalled/disabled after having installed it. This is because it patches some files on-disk when it is first installed. Uninstallation will be made easier in the future, but for now, after removing JellyfinLoader:
- Shut down your Jellyfin server.
- Go to your Jellyfin server directory
- Delete `Emby.Server.Implementations.dll`
- Rename the backup (`Emby.Server.Implementations.dll.bak`) that JellyfinLoader created back to `Emby.Server.Implementations.dll`.

Additionally, only Jellyfin `10.10.7.0` is currently supported.

Finally, any early loaded plugins (specified in their `loader.json` file), will not be unloaded and reloaded when soft restarting (through the web interface) because they are loaded into the main assembly context. 99% of the time, however, this isn't a problem at all.
