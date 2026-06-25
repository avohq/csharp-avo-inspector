using System.Collections;
using System.Collections.Generic;

namespace Avo.Inspector
{
    /// <summary>
    /// Pure, synchronous schema extraction (SPEC.md §9). Maps an arbitrary tree of native event
    /// properties into the Inspector schema shape (a list of <see cref="SchemaEntry"/>).
    /// </summary>
    /// <remarks>
    /// This parser has no try/catch of its own and MAY throw on pathological input; the safe
    /// boundary is <see cref="AvoInspector.ExtractSchema"/>, which catches and returns an empty
    /// list (SPEC.md §4.3). The parser distinguishes <c>int</c> from <c>float</c> by the native
    /// runtime type (SPEC.md §9.3.1): a statically-typed SDK MUST classify any floating value —
    /// including a whole-valued <c>0.0</c> — as <c>"float"</c>.
    /// </remarks>
    public static class AvoSchemaParser
    {
        /// <summary>
        /// Maximum recursion depth before truncation (SPEC.md §9.3.2). Beyond this, a complex value
        /// is included as <c>propertyType: "object"</c> with empty children rather than recursed
        /// into. Not exercised by the conformance fixtures (max nesting tested is 3 levels).
        /// </summary>
        private const int MaxDepth = 10;

        /// <summary>
        /// Extracts the schema for an event-property map (SPEC.md §9.1). Returns an empty list for a
        /// <c>null</c> map (SPEC.md §4.3, §9.2).
        /// </summary>
        public static List<SchemaEntry> ExtractSchema(IDictionary<string, object?>? eventProperties)
        {
            if (eventProperties == null)
            {
                return new List<SchemaEntry>();
            }

            return MapObject(eventProperties, 0);
        }

        /// <summary>
        /// Recursively maps a native value (SPEC.md §9.2 <c>mapping</c>). Returns a
        /// <c>List&lt;SchemaEntry&gt;</c> for an object, a <c>List&lt;object?&gt;</c> for an array,
        /// or a type string for a scalar.
        /// </summary>
        private static object Mapping(object? value, int depth)
        {
            if (value is IDictionary<string, object?> dict)
            {
                return MapObject(dict, depth);
            }
            if (value is string)
            {
                return GetPropValueType(value);
            }
            if (value is IEnumerable en)
            {
                return MapArray(en, depth);
            }
            return GetPropValueType(value);
        }

        private static List<SchemaEntry> MapObject(IDictionary<string, object?> dict, int depth)
        {
            var result = new List<SchemaEntry>(dict.Count);
            foreach (var kv in dict)
            {
                var val = kv.Value;
                var entry = new SchemaEntry(kv.Key, GetPropValueType(val));
                if (IsComplex(val))
                {
                    if (depth >= MaxDepth)
                    {
                        // SPEC.md §9.3.2 depth truncation: object with empty children, no descent.
                        entry.PropertyType = "object";
                        entry.Children = new List<SchemaEntry>();
                    }
                    else
                    {
                        entry.Children = Mapping(val, depth + 1);
                    }
                }
                result.Add(entry);
            }
            return result;
        }

        private static List<object?> MapArray(IEnumerable array, int depth)
        {
            var mapped = new List<object?>();
            foreach (var element in array)
            {
                if (IsComplex(element) && depth >= MaxDepth)
                {
                    mapped.Add("object"); // SPEC.md §9.3.2 depth truncation for nested array elements.
                }
                else
                {
                    mapped.Add(Mapping(element, depth + 1));
                }
            }
            return RemoveDuplicates(mapped);
        }

        /// <summary>
        /// Resolves the property-value type (SPEC.md §9.2 <c>getPropValueType</c>): a
        /// <c>list(&lt;elemType&gt;)</c> wrapper for arrays (element type from the first element;
        /// empty or null-first defaults to <c>list(string)</c>), otherwise the basic scalar type.
        /// </summary>
        private static string GetPropValueType(object? value)
        {
            if (value is IDictionary<string, object?>)
            {
                return "object";
            }
            if (!(value is string) && value is IEnumerable en)
            {
                var first = FirstOrNull(en);
                if (first == null)
                {
                    return "list(string)"; // empty array, or first element null/undefined.
                }
                return "list(" + GetBasicPropType(first) + ")";
            }
            return GetBasicPropType(value);
        }

        /// <summary>Classifies a scalar value (SPEC.md §9.2/§9.3 <c>getBasicPropType</c>).</summary>
        private static string GetBasicPropType(object? value)
        {
            switch (value)
            {
                case null:
                    return "null";
                case string:
                    return "string";
                case bool:
                    return "boolean";
                case sbyte:
                case byte:
                case short:
                case ushort:
                case int:
                case uint:
                case long:
                case ulong:
                    return "int";
                case float:
                case double:
                case decimal:
                    // SPEC.md §9.3.1: the declared/runtime float type is authoritative, so a
                    // whole-valued float such as 0.0 MUST classify as "float".
                    return "float";
                case IDictionary<string, object?>:
                    return "object";
                case IEnumerable:
                    // A nested array (or other non-string collection) is an object on the wire
                    // (parity with JS `typeof [] === "object"`, SPEC.md §9.3.4).
                    return "object";
                default:
                    return "unknown";
            }
        }

        /// <summary>True when a value gets a <c>children</c> array (a non-null object or array).</summary>
        private static bool IsComplex(object? value)
            => value is IDictionary<string, object?> || (!(value is string) && value is IEnumerable);

        private static object? FirstOrNull(IEnumerable source)
        {
            foreach (var item in source)
            {
                return item;
            }
            return null;
        }

        /// <summary>
        /// Deduplicates mapped array elements (SPEC.md §9.3.3): primitive type strings by value,
        /// nested structures by structural deep equality, preserving first-seen order.
        /// </summary>
        private static List<object?> RemoveDuplicates(List<object?> items)
        {
            var output = new List<object?>();
            foreach (var item in items)
            {
                var duplicate = false;
                foreach (var seen in output)
                {
                    if (DeepEqual(seen, item))
                    {
                        duplicate = true;
                        break;
                    }
                }
                if (!duplicate)
                {
                    output.Add(item);
                }
            }
            return output;
        }

        /// <summary>
        /// Structural deep equality over mapped values: type strings, <see cref="SchemaEntry"/>
        /// objects, and (possibly nested) arrays of either (SPEC.md §9.3.3).
        /// </summary>
        internal static bool DeepEqual(object? a, object? b)
        {
            if (ReferenceEquals(a, b))
            {
                return true;
            }
            if (a == null || b == null)
            {
                return false;
            }
            if (a is string sa)
            {
                return b is string sb && sa == sb;
            }
            if (a is SchemaEntry ea)
            {
                return b is SchemaEntry eb
                    && ea.PropertyName == eb.PropertyName
                    && ea.PropertyType == eb.PropertyType
                    && DeepEqual(ea.Children, eb.Children);
            }
            if (!(a is string) && !(b is string) && a is IEnumerable la && b is IEnumerable lb)
            {
                var itA = la.GetEnumerator();
                var itB = lb.GetEnumerator();
                while (true)
                {
                    var hasA = itA.MoveNext();
                    var hasB = itB.MoveNext();
                    if (hasA != hasB)
                    {
                        return false;
                    }
                    if (!hasA)
                    {
                        return true;
                    }
                    if (!DeepEqual(itA.Current, itB.Current))
                    {
                        return false;
                    }
                }
            }
            return a.Equals(b);
        }
    }
}
