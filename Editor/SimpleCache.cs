using System;
using System.Collections.Generic;

namespace MyUnityHub
{
    /// <summary>Time-boxed in-memory cache. Minimizes API calls between refreshes.</summary>
    internal class SimpleCache<T>
    {
        readonly TimeSpan _ttl;
        readonly Dictionary<string, (DateTime t, T v)> _map = new Dictionary<string, (DateTime, T)>();

        public SimpleCache(double ttlSeconds) { _ttl = TimeSpan.FromSeconds(ttlSeconds); }

        public bool TryGet(string key, out T value)
        {
            if (_map.TryGetValue(key, out var e) && DateTime.UtcNow - e.t < _ttl)
            {
                value = e.v;
                return true;
            }
            value = default;
            return false;
        }

        public void Set(string key, T value) => _map[key] = (DateTime.UtcNow, value);
        public void Clear() => _map.Clear();
    }
}

