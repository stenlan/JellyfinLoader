using JellyfinLoader.AssemblyLoading;
using System.Reflection;
using System.Runtime.Loader;

namespace JellyfinLoader.Models
{
    internal class JLAssemblyData(AssemblyLoadContext alc, int? depPoolID)
    {
        public Assembly? Assembly { get; init; }

        public AssemblyLoadContext AssemblyLoadContext { get; init; } = alc;

        public int? DepPoolId { get; init; } = depPoolID;
    }
}
