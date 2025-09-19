namespace JellyfinLoader.Helpers
{
    internal class JLHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            return Utils.HttpClient;
        }
    }
}
