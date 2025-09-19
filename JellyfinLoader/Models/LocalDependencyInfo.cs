namespace JellyfinLoader.Models
{
    internal record LocalDependencyInfo(string Manifest, Guid ID, List<Version> Versions, Guid Dependent);
}
