namespace JellyfinLoader
{
	[Serializable]
	public class DependencyResolverException : Exception
	{
		public DependencyResolverException() { }
		public DependencyResolverException(string message) : base(message) { }
		public DependencyResolverException(string message, Exception inner) : base(message, inner) { }
	}
}
