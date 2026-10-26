using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Avo.Inspector.Internal.EventSpec
{
    /// <summary>
    /// Per-instance cache of fetched event specs, keyed by <see cref="MakeKey"/>. A port of the Node
    /// SDK's <c>AvoEventSpecCache</c>: a spec is kept for 60 s or 50 uses, whichever comes first; a
    /// failed fetch is cached too (as <c>null</c>) so it is not repeated for every event — a 404 for
    /// 60 s, any other failure for 5 s — and is not refreshed by use. At most 50 entries are kept,
    /// least recently used evicted first. Thread-safe.
    /// </summary>
    internal sealed class EventSpecCache
    {
        internal const int MaxEntries = 50;
        internal const int MaxUses = 50;
        internal static readonly TimeSpan SpecTtl = TimeSpan.FromSeconds(60);
        internal static readonly TimeSpan TransientFailureTtl = TimeSpan.FromSeconds(5);

        private readonly object _lock = new object();
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);

        private sealed class Entry
        {
            public EventSpecResponse? Value;
            public long ExpiresAt;
            public int Uses;
            public long LastAccessed;
        }

        /// <summary>
        /// The key of one spec fetch. Every part is length-prefixed, so no event name or stream id
        /// can make two different fetches share a key. The gateway coordinates are part of it, so a
        /// gateway-input spec and an output spec for the same event never share an entry.
        /// </summary>
        internal static string MakeKey(string streamId, string eventName, string? originHint, string? outputReference)
        {
            return Part(streamId) + Part(eventName) + Part(originHint) + Part(outputReference);
        }

        private static string Part(string? value)
            => value == null ? "-;" : value.Length + ":" + value + ";";

        /// <summary>
        /// True on a hit, with <paramref name="value"/> the cached spec, or <c>null</c> for a cached
        /// failure (the event is then sent without validation). False on a miss.
        /// </summary>
        public bool TryGet(string key, out EventSpecResponse? value)
        {
            var now = Stopwatch.GetTimestamp();
            lock (_lock)
            {
                value = null;
                if (!_entries.TryGetValue(key, out var entry))
                {
                    return false;
                }
                if (now > entry.ExpiresAt)
                {
                    _entries.Remove(key);
                    return false;
                }
                // A spec is refetched after MaxUses uses; a cached failure has nothing to refresh.
                entry.Uses++;
                if (entry.Value != null && entry.Uses >= MaxUses)
                {
                    _entries.Remove(key);
                    return false;
                }
                entry.LastAccessed = now;
                value = entry.Value;
                return true;
            }
        }

        /// <summary>Stores a fetched spec, or <c>null</c> for a failed fetch, for <paramref name="ttl"/>.</summary>
        public void Set(string key, EventSpecResponse? value, TimeSpan ttl)
        {
            var now = Stopwatch.GetTimestamp();
            lock (_lock)
            {
                _entries[key] = new Entry
                {
                    Value = value,
                    ExpiresAt = now + (long)(ttl.TotalSeconds * Stopwatch.Frequency),
                    Uses = 0,
                    LastAccessed = now
                };
                if (_entries.Count > MaxEntries)
                {
                    PurgeExpired(now);
                }
                while (_entries.Count > MaxEntries)
                {
                    EvictLeastRecentlyUsed();
                }
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _entries.Clear();
            }
        }

        internal int CountForTesting
        {
            get { lock (_lock) { return _entries.Count; } }
        }

        private void PurgeExpired(long now)
        {
            var expired = new List<string>();
            foreach (var pair in _entries)
            {
                if (now > pair.Value.ExpiresAt)
                {
                    expired.Add(pair.Key);
                }
            }
            foreach (var key in expired)
            {
                _entries.Remove(key);
            }
        }

        private void EvictLeastRecentlyUsed()
        {
            string? oldestKey = null;
            var oldest = long.MaxValue;
            foreach (var pair in _entries)
            {
                if (pair.Value.LastAccessed < oldest)
                {
                    oldest = pair.Value.LastAccessed;
                    oldestKey = pair.Key;
                }
            }
            if (oldestKey != null)
            {
                _entries.Remove(oldestKey);
            }
        }
    }
}
