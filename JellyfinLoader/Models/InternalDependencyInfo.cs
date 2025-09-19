namespace JellyfinLoader.Models
{
    internal record InternalDependencyInfo(string Manifest, Guid ID, List<Version> Versions, Guid Dependent);
}
