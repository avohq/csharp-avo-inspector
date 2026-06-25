using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.Json;

namespace Avo.Inspector.Conformance
{
    /// <summary>
    /// Materializes parsed JSON into the SDK's native value types (runner-contract: "the host
    /// materializes JSON numbers into declared types"). The conversion:
    /// <list type="bullet">
    ///   <item>preserves object key order (so extracted schema property order is deterministic);</item>
    ///   <item>preserves the int-vs-float distinction from the literal source (SPEC.md §9.3.1.1) —
    ///   a JSON integer literal becomes <see cref="long"/>, a float literal becomes
    ///   <see cref="double"/>.</item>
    /// </list>
    /// </summary>
    internal static class JsonInterop
    {
        /// <summary>Converts a <see cref="JsonElement"/> to a native value, or <c>null</c>.</summary>
        public static object? ToNative(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    var map = new OrderedPropertyDictionary();
                    foreach (var prop in element.EnumerateObject())
                    {
                        map[prop.Name] = ToNative(prop.Value);
                    }
                    return map;

                case JsonValueKind.Array:
                    var list = new List<object?>();
                    foreach (var item in element.EnumerateArray())
                    {
                        list.Add(ToNative(item));
                    }
                    return list;

                case JsonValueKind.String:
                    return element.GetString();

                case JsonValueKind.Number:
                    return ToNativeNumber(element);

                case JsonValueKind.True:
                    return true;

                case JsonValueKind.False:
                    return false;

                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                default:
                    return null;
            }
        }

        /// <summary>
        /// Converts the top-level event-properties payload to an <see cref="IDictionary{TKey,TValue}"/>
        /// (or <c>null</c> for a JSON <c>null</c> / non-object), as the SDK's <c>ExtractSchema</c>
        /// and <c>TrackSchemaFromEvent</c> expect.
        /// </summary>
        public static IDictionary<string, object?>? ToPropertyMap(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                return (IDictionary<string, object?>)ToNative(element)!;
            }
            return null;
        }

        private static object ToNativeNumber(JsonElement element)
        {
            // A literal with a fractional or exponent part is a float (SPEC.md §9.3.1.1); otherwise
            // an integer. This preserves a fixture's `3.14` as a double and `3` as a long.
            var raw = element.GetRawText();
            if (raw.IndexOf('.') >= 0 || raw.IndexOf('e') >= 0 || raw.IndexOf('E') >= 0)
            {
                return element.GetDouble();
            }
            if (element.TryGetInt64(out var l))
            {
                return l;
            }
            // Integer literal too large for Int64 — fall back to double.
            return element.GetDouble();
        }
    }

    /// <summary>
    /// A minimal insertion-order-preserving <see cref="IDictionary{TKey,TValue}"/> with
    /// <see cref="string"/> keys. The SDK enumerates an event-property map in its iteration order to
    /// produce schema property order, so the harness feeds it an ordered map (a plain
    /// <see cref="Dictionary{TKey,TValue}"/> does not guarantee enumeration order).
    /// </summary>
    internal sealed class OrderedPropertyDictionary : IDictionary<string, object?>
    {
        private readonly List<string> _order = new List<string>();
        private readonly Dictionary<string, object?> _map = new Dictionary<string, object?>();

        public object? this[string key]
        {
            get => _map[key];
            set
            {
                if (!_map.ContainsKey(key))
                {
                    _order.Add(key);
                }
                _map[key] = value;
            }
        }

        public ICollection<string> Keys => _order.AsReadOnly();

        public ICollection<object?> Values
        {
            get
            {
                var values = new List<object?>(_order.Count);
                foreach (var key in _order)
                {
                    values.Add(_map[key]);
                }
                return values;
            }
        }

        public int Count => _order.Count;

        public bool IsReadOnly => false;

        public void Add(string key, object? value)
        {
            if (_map.ContainsKey(key))
            {
                throw new ArgumentException("An item with the same key has already been added: " + key);
            }
            _order.Add(key);
            _map[key] = value;
        }

        public void Add(KeyValuePair<string, object?> item) => Add(item.Key, item.Value);

        public void Clear()
        {
            _order.Clear();
            _map.Clear();
        }

        public bool Contains(KeyValuePair<string, object?> item)
            => _map.TryGetValue(item.Key, out var value) && EqualityComparer<object?>.Default.Equals(value, item.Value);

        public bool ContainsKey(string key) => _map.ContainsKey(key);

        public void CopyTo(KeyValuePair<string, object?>[] array, int arrayIndex)
        {
            foreach (var kv in this)
            {
                array[arrayIndex++] = kv;
            }
        }

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        {
            foreach (var key in _order)
            {
                yield return new KeyValuePair<string, object?>(key, _map[key]);
            }
        }

        public bool Remove(string key)
        {
            if (_map.Remove(key))
            {
                _order.Remove(key);
                return true;
            }
            return false;
        }

        public bool Remove(KeyValuePair<string, object?> item)
            => Contains(item) && Remove(item.Key);

        public bool TryGetValue(string key, out object? value) => _map.TryGetValue(key, out value);

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
