using Emby.Server.Implementations.Plugins;
using System.Runtime.Loader;

namespace JellyfinLoader.AssemblyLoading
{
    internal class JLLoadContext : AssemblyLoadContext
    {
        public JLLoadContext() : base(true) {}
    }
}
