namespace JellyfinLoader.Helpers
{
    internal static class Extensions
    {
        public static TValue GetOrAdd<TKey, TValue>(this IDictionary<TKey, TValue> dict, TKey key, TValue value)
        {
            var hasEntry = dict.TryGetValue(key, out var outVar);
            if (!hasEntry)
            {
                dict[key] = value;
                return value;
            }
            return outVar!;
        }
    }
}
