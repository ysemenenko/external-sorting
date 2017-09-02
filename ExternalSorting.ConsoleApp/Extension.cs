using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace ExternalSorting.ConsoleApp
{
    public static class Extension
    {
        public static void AddRange<T>(this ConcurrentBag<T> @this, IEnumerable<T> toAdd)
        {
            toAdd.AsParallel().ForAll(t => @this.Add(t));
        }

        public static void AddRange<K, T>(this ConcurrentDictionary<int, int> @this, IEnumerable<int> toAdd)
        {
            toAdd.AsParallel().ForAll(t => @this.TryAdd(t, t));
        }
    }
}
