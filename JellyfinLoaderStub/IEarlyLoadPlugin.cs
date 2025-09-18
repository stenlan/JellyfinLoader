namespace JellyfinLoader
{
    /// <summary>
    /// Interface that allows early loaded plugins to hook into the StartServer function, and/or perform very early logic in their constructor.
    /// </summary>
    public interface IEarlyLoadPlugin
    {
        public void OnServerStart(bool coldStart) { }
    }
}
