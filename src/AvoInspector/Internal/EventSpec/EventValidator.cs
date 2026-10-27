using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Avo.Inspector.Internal.EventSpec
{
    /// <summary>
    /// Checks event property values against the value constraints of an event spec: pinned values,
    /// allowed values, regex patterns and min/max ranges, on a property and on the properties of an
    /// object (or of each object in a list), one level deep. A port of the Node SDK's
    /// <c>EventValidator</c> (node-avo-inspector 2.0.0), which follows the web and Android SDKs.
    /// </summary>
    /// <remarks>
    /// <para>Each constraint applies to the tracking-plan event IDs (base event and variants) the spec
    /// lists for it, so a value can pass for one variant and fail for another. Types are not checked:
    /// Avo compares the reported schema with the tracking plan, and the spec's type names differ from
    /// the schema's. <c>required</c> only decides whether a <c>null</c> or missing value is checked
    /// against the constraints.</para>
    /// <para>Validation runs on the track path, so it is bounded: a list of more than 1,000 items is
    /// not checked, a string longer than 10,000 characters is not checked against regex patterns, regex
    /// patterns run on .NET's linear-time engine where it can take them, and each regex match has a
    /// 100 ms budget (a pattern that runs out is skipped from then on). Logs carry spec
    /// strings and counts, never property values.</para>
    /// </remarks>
    internal static class EventValidator
    {
        // A property (depth 0) and the properties of an object or of each object in a list (depth 1).
        private const int MaxChildDepth = 2;
        private const int MaxValidatedListItems = 1_000;
        private const int MaxRegexInputLength = 10_000;

        // Compiled patterns and parsed allowed-value lists, keyed by their wire string. Both come from
        // the tracking plan, so they stay few; the bound only guards a long-lived process against a
        // plan that keeps changing. null marks a pattern or list that is skipped (invalid).
        private const int MaxCachedConstraints = 1_000;
        private static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromMilliseconds(100);
        private static readonly object CacheLock = new object();
        private static readonly Dictionary<string, Regex?> RegexCache = new Dictionary<string, Regex?>(StringComparer.Ordinal);
        private static readonly Dictionary<string, HashSet<string>?> AllowedValuesCache =
            new Dictionary<string, HashSet<string>?>(StringComparer.Ordinal);

        // A property absent from its object. Compared as "undefined", like JavaScript's String(undefined).
        private static readonly object Missing = new object();

        /// <summary>
        /// Validates the properties of <paramref name="eventProperties"/> that the spec constrains.
        /// Returns the outcome of each property that failed for at least one event ID, or whose
        /// object properties did; properties the spec does not constrain are left out.
        /// </summary>
        public static Dictionary<string, PropertyValidationOutcome> Validate(
            EventSpecResponse spec,
            IReadOnlyList<SchemaEntry> eventSchema,
            IDictionary<string, object?>? eventProperties,
            bool shouldLog)
        {
            var constraintsByName = CollectConstraintsByName(spec.Events);
            var results = new Dictionary<string, PropertyValidationOutcome>(StringComparer.Ordinal);
            foreach (var entry in eventSchema)
            {
                if (results.ContainsKey(entry.PropertyName)
                    || !constraintsByName.TryGetValue(entry.PropertyName, out var constraints))
                {
                    continue;
                }
                var value = eventProperties != null && eventProperties.TryGetValue(entry.PropertyName, out var raw)
                    ? raw
                    : Missing;
                var outcome = ValidateProperty(value, constraints, 0, shouldLog);
                if (outcome != null)
                {
                    results[entry.PropertyName] = outcome;
                }
            }
            return results;
        }

        // ----- constraints across events ---------------------------------------------------------

        /// <summary>
        /// One lookup of constraints per property name across every event in the spec. Name mapping
        /// can match several events; each constraint keeps its own event IDs, so merging is a union.
        /// </summary>
        private static Dictionary<string, PropertyConstraints> CollectConstraintsByName(List<EventSpecEntry> events)
        {
            var result = new Dictionary<string, PropertyConstraints>(StringComparer.Ordinal);
            foreach (var spec in events)
            {
                foreach (var pair in spec.Props)
                {
                    result[pair.Key] = result.TryGetValue(pair.Key, out var existing)
                        ? Merge(existing, pair.Value)
                        : pair.Value;
                }
            }
            return result;
        }

        // Builds a new object: the parsed spec is cached and shared by every track, so it is never written.
        private static PropertyConstraints Merge(PropertyConstraints a, PropertyConstraints b)
        {
            var merged = new PropertyConstraints
            {
                Type = a.Type,
                // A missing value fails only where every matching event requires the property.
                Required = a.Required && b.Required,
                IsList = a.IsList || b.IsList,
                PinnedValues = MergeIdMappings(a.PinnedValues, b.PinnedValues),
                AllowedValues = MergeIdMappings(a.AllowedValues, b.AllowedValues),
                RegexPatterns = MergeIdMappings(a.RegexPatterns, b.RegexPatterns),
                MinMaxRanges = MergeIdMappings(a.MinMaxRanges, b.MinMaxRanges)
            };
            if (a.Children != null || b.Children != null)
            {
                var children = new Dictionary<string, PropertyConstraints>(StringComparer.Ordinal);
                foreach (var source in new[] { a.Children, b.Children })
                {
                    if (source == null)
                    {
                        continue;
                    }
                    foreach (var pair in source)
                    {
                        children[pair.Key] = children.TryGetValue(pair.Key, out var existing)
                            ? Merge(existing, pair.Value)
                            : pair.Value;
                    }
                }
                merged.Children = children;
            }
            return merged;
        }

        private static Dictionary<string, List<string>>? MergeIdMappings(
            Dictionary<string, List<string>>? a,
            Dictionary<string, List<string>>? b)
        {
            if (a == null || b == null)
            {
                return a ?? b;
            }
            var merged = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var source in new[] { a, b })
            {
                foreach (var pair in source)
                {
                    if (!merged.TryGetValue(pair.Key, out var ids))
                    {
                        merged[pair.Key] = new List<string>(pair.Value);
                        continue;
                    }
                    foreach (var id in pair.Value)
                    {
                        if (!ids.Contains(id))
                        {
                            ids.Add(id);
                        }
                    }
                }
            }
            return merged;
        }

        // ----- validation ------------------------------------------------------------------------

        private static PropertyValidationOutcome? ValidateProperty(
            object? value, PropertyConstraints constraints, int depth, bool shouldLog)
        {
            if (depth >= MaxChildDepth)
            {
                return null;
            }
            if (constraints.IsList)
            {
                return ValidateList(value, constraints, depth, shouldLog);
            }
            if (constraints.Children != null)
            {
                return ValidateObject(value, constraints.Children, depth, shouldLog);
            }
            // An optional property sent as null, or not sent, has no value to check.
            if ((value == null || value == Missing) && !constraints.Required)
            {
                return null;
            }
            var failedIds = new IdSet();
            CheckValue(value, constraints, failedIds, shouldLog);
            return failedIds.ToOutcome();
        }

        /// <summary>
        /// A list of objects is checked property by property across every item: an event ID fails a
        /// child property when any item's value fails it. A list of primitives is checked item by item.
        /// A value that is not a list is not checked (its type is reported in the schema).
        /// </summary>
        private static PropertyValidationOutcome? ValidateList(
            object? value, PropertyConstraints constraints, int depth, bool shouldLog)
        {
            if (!RawValues.IsList(value))
            {
                return null;
            }
            var items = new List<object?>();
            foreach (var item in (System.Collections.IEnumerable)value!)
            {
                if (items.Count == MaxValidatedListItems)
                {
                    Logger.Error(shouldLog, "List of more than " + MaxValidatedListItems + " items not validated.");
                    return null;
                }
                items.Add(item);
            }

            if (constraints.Children != null)
            {
                Dictionary<string, PropertyValidationOutcome>? children = null;
                foreach (var pair in constraints.Children)
                {
                    var failedIds = new IdSet();
                    foreach (var item in items)
                    {
                        var outcome = ValidateProperty(ChildValue(item, pair.Key), pair.Value, depth + 1, shouldLog);
                        if (outcome != null)
                        {
                            failedIds.AddAll(outcome.FailedEventIds);
                        }
                    }
                    var childOutcome = failedIds.ToOutcome();
                    if (childOutcome != null)
                    {
                        children ??= new Dictionary<string, PropertyValidationOutcome>(StringComparer.Ordinal);
                        children[pair.Key] = childOutcome;
                    }
                }
                return children != null ? new PropertyValidationOutcome(new List<string>(), children) : null;
            }

            var ids = new IdSet();
            foreach (var item in items)
            {
                CheckValue(item, constraints, ids, shouldLog);
            }
            return ids.ToOutcome();
        }

        // An object's children are checked even when the object is null or not an object: a required,
        // constrained child is then missing, and fails (as in the web SDK).
        private static PropertyValidationOutcome? ValidateObject(
            object? value, Dictionary<string, PropertyConstraints> childConstraints, int depth, bool shouldLog)
        {
            Dictionary<string, PropertyValidationOutcome>? children = null;
            foreach (var pair in childConstraints)
            {
                var outcome = ValidateProperty(ChildValue(value, pair.Key), pair.Value, depth + 1, shouldLog);
                if (outcome != null)
                {
                    children ??= new Dictionary<string, PropertyValidationOutcome>(StringComparer.Ordinal);
                    children[pair.Key] = outcome;
                }
            }
            return children != null ? new PropertyValidationOutcome(new List<string>(), children) : null;
        }

        private static object? ChildValue(object? container, string name)
            => RawValues.TryGetChild(container, name, out var child) ? child : Missing;

        /// <summary>Adds to <paramref name="failedIds"/> the event IDs whose constraints the value breaks.</summary>
        private static void CheckValue(object? value, PropertyConstraints constraints, IdSet failedIds, bool shouldLog)
        {
            if (constraints.PinnedValues != null || constraints.AllowedValues != null)
            {
                var actual = value == Missing ? "undefined" : RawValues.ComparableString(value);
                if (constraints.PinnedValues != null)
                {
                    foreach (var pair in constraints.PinnedValues)
                    {
                        if (actual != pair.Key)
                        {
                            failedIds.AddAll(pair.Value);
                        }
                    }
                }
                if (constraints.AllowedValues != null)
                {
                    foreach (var pair in constraints.AllowedValues)
                    {
                        // A list that is not a JSON array of strings is skipped: nothing fails it.
                        var allowed = ParseAllowedValues(pair.Key, shouldLog);
                        if (allowed != null && !allowed.Contains(actual))
                        {
                            failedIds.AddAll(pair.Value);
                        }
                    }
                }
            }
            if (constraints.RegexPatterns != null)
            {
                CheckRegexPatterns(value, constraints.RegexPatterns, failedIds, shouldLog);
            }
            if (constraints.MinMaxRanges != null)
            {
                CheckMinMaxRanges(value, constraints.MinMaxRanges, failedIds, shouldLog);
            }
        }

        private static void CheckRegexPatterns(
            object? value, Dictionary<string, List<string>> regexPatterns, IdSet failedIds, bool shouldLog)
        {
            var isString = RawValues.TryGetString(value, out var text);
            if (isString && text.Length > MaxRegexInputLength)
            {
                Logger.Error(shouldLog, "String of " + text.Length + " characters not checked against regex patterns (limit " + MaxRegexInputLength + ").");
                return;
            }
            foreach (var pair in regexPatterns)
            {
                // Only a string can match; an invalid pattern is skipped (nothing fails it).
                if (!isString)
                {
                    failedIds.AddAll(pair.Value);
                    continue;
                }
                var regex = CompileRegex(pair.Key, shouldLog);
                if (regex == null)
                {
                    continue;
                }
                bool matched;
                try
                {
                    matched = regex.IsMatch(text);
                }
                catch (RegexMatchTimeoutException)
                {
                    Logger.Error(shouldLog, "Regex pattern skipped (match timed out): " + pair.Key);
                    // Never run a pattern that has timed out again, on later items or events.
                    CacheConstraint<Regex?>(RegexCache, pair.Key, null);
                    continue;
                }
                if (!matched)
                {
                    failedIds.AddAll(pair.Value);
                }
            }
        }

        /// <summary>
        /// <c>"min,max"</c> with either bound possibly empty: <c>"0,"</c> has no maximum, <c>",100"</c>
        /// no minimum. Only a number can be in range; NaN never is. A range that does not parse is skipped.
        /// </summary>
        private static void CheckMinMaxRanges(
            object? value, Dictionary<string, List<string>> minMaxRanges, IdSet failedIds, bool shouldLog)
        {
            if (!RawValues.TryGetNumber(value, out var actual))
            {
                actual = double.NaN;
            }
            foreach (var pair in minMaxRanges)
            {
                if (!TryParseRange(pair.Key, out var min, out var max))
                {
                    Logger.Error(shouldLog, "Invalid min/max range skipped: " + pair.Key);
                    continue;
                }
                if (double.IsNaN(actual) || actual < min || actual > max)
                {
                    failedIds.AddAll(pair.Value);
                }
            }
        }

        internal static bool TryParseRange(string range, out double min, out double max)
        {
            min = double.NegativeInfinity;
            max = double.PositiveInfinity;
            var parts = range.Split(',');
            if (parts.Length != 2)
            {
                return false;
            }
            var minPart = parts[0].Trim();
            var maxPart = parts[1].Trim();
            if (minPart.Length > 0 && !double.TryParse(minPart, NumberStyles.Float, CultureInfo.InvariantCulture, out min))
            {
                return false;
            }
            if (maxPart.Length > 0 && !double.TryParse(maxPart, NumberStyles.Float, CultureInfo.InvariantCulture, out max))
            {
                return false;
            }
            return !double.IsNaN(min) && !double.IsNaN(max);
        }

        private static HashSet<string>? ParseAllowedValues(string json, bool shouldLog)
        {
            lock (CacheLock)
            {
                if (AllowedValuesCache.TryGetValue(json, out var cached))
                {
                    return cached;
                }
            }
            HashSet<string>? allowed = null;
            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        allowed = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var item in doc.RootElement.EnumerateArray())
                        {
                            if (item.ValueKind != JsonValueKind.String)
                            {
                                allowed = null;
                                break;
                            }
                            allowed.Add(item.GetString()!);
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // Left null: skipped.
            }
            if (allowed == null)
            {
                Logger.Error(shouldLog, "Invalid allowed values skipped: " + json);
            }
            CacheConstraint(AllowedValuesCache, json, allowed);
            return allowed;
        }

        /// <summary>
        /// Compiles a tracking-plan pattern. On .NET 7 and later a pattern is compiled first with
        /// <see cref="RegexOptions"/><c>.NonBacktracking</c>, whose matching time is linear in the
        /// input, so no pattern can backtrack catastrophically (the guarantee Go's RE2 and Java's
        /// RE2J give). That engine cannot take <c>RegexOptions.ECMAScript</c>, so
        /// <see cref="ToLinearPattern"/> first rewrites <c>\d</c> and <c>\w</c> to JavaScript's
        /// ASCII classes. A pattern it cannot take (lookarounds, backreferences, <c>\b</c>) and every
        /// pattern on older runtimes use the backtracking engine: ECMAScript semantics first, the
        /// default .NET syntax when that option rejects the pattern. Every match has a timeout either
        /// way, which on the backtracking engine stands in for the Node SDK's static unsafe-pattern
        /// check.
        /// </summary>
        private static Regex? CompileRegex(string pattern, bool shouldLog)
        {
            lock (CacheLock)
            {
                if (RegexCache.TryGetValue(pattern, out var cached))
                {
                    return cached;
                }
            }
            var regex = CompileLinear(pattern) ?? CompileBacktracking(pattern, shouldLog);
            CacheConstraint(RegexCache, pattern, regex);
            return regex;
        }

        private static Regex? CompileLinear(string pattern)
        {
#if NET7_0_OR_GREATER
            var linear = ToLinearPattern(pattern);
            if (linear == null)
            {
                return null;
            }
            try
            {
                return new Regex(linear, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, RegexMatchTimeout);
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (NotSupportedException)
            {
                return null;
            }
#else
            return null;
#endif
        }

        private static Regex? CompileBacktracking(string pattern, bool shouldLog)
        {
            try
            {
                return new Regex(pattern, RegexOptions.ECMAScript, RegexMatchTimeout);
            }
            catch (ArgumentException)
            {
                try
                {
                    return new Regex(pattern, RegexOptions.CultureInvariant, RegexMatchTimeout);
                }
                catch (ArgumentException)
                {
                    Logger.Error(shouldLog, "Invalid regex pattern skipped: " + pattern);
                    return null;
                }
            }
        }

        /// <summary>
        /// Rewrites a JavaScript pattern for the default .NET syntax so that <c>\d</c>, <c>\D</c>,
        /// <c>\w</c> and <c>\W</c> keep JavaScript's ASCII meaning (.NET's match any Unicode digit
        /// or letter). Returns <c>null</c> for a pattern whose meaning the rewrite cannot keep:
        /// <c>\D</c> or <c>\W</c> inside a character class, <c>\b</c> or <c>\B</c> (word boundaries
        /// use .NET's Unicode word characters), an empty class (<c>[]</c>, <c>[^]</c>) or <c>-[</c>
        /// inside a class, which .NET parses differently. Such a pattern goes to the backtracking
        /// engine instead.
        /// </summary>
        internal static string? ToLinearPattern(string pattern)
        {
            var result = new System.Text.StringBuilder(pattern.Length + 16);
            var inClass = false;
            for (var i = 0; i < pattern.Length; i++)
            {
                var c = pattern[i];
                if (c == '\\')
                {
                    if (i + 1 >= pattern.Length)
                    {
                        return null;
                    }
                    var next = pattern[++i];
                    switch (next)
                    {
                        case 'd':
                            result.Append(inClass ? "0-9" : "[0-9]");
                            break;
                        case 'w':
                            result.Append(inClass ? "a-zA-Z0-9_" : "[a-zA-Z0-9_]");
                            break;
                        case 'D':
                        case 'W':
                            if (inClass)
                            {
                                return null;
                            }
                            result.Append(next == 'D' ? "[^0-9]" : "[^a-zA-Z0-9_]");
                            break;
                        case 'b':
                        case 'B':
                            if (!inClass)
                            {
                                return null;
                            }
                            result.Append('\\').Append(next);
                            break;
                        default:
                            result.Append('\\').Append(next);
                            break;
                    }
                    continue;
                }
                if (!inClass && c == '[')
                {
                    var close = i + 1 < pattern.Length && pattern[i + 1] == '^' ? i + 2 : i + 1;
                    if (close < pattern.Length && pattern[close] == ']')
                    {
                        return null;
                    }
                    inClass = true;
                }
                else if (inClass && c == ']')
                {
                    inClass = false;
                }
                else if (inClass && c == '[' && pattern[i - 1] == '-')
                {
                    // "-[" inside a class is .NET's class subtraction; in JavaScript it is literal.
                    return null;
                }
                result.Append(c);
            }
            return result.ToString();
        }

        private static void CacheConstraint<T>(Dictionary<string, T> cache, string key, T value)
        {
            lock (CacheLock)
            {
                if (cache.Count >= MaxCachedConstraints)
                {
                    cache.Clear();
                }
                cache[key] = value;
            }
        }

        /// <summary>Event IDs in first-seen order, without duplicates.</summary>
        private sealed class IdSet
        {
            private readonly List<string> _ids = new List<string>();
            private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);

            public void AddAll(List<string> ids)
            {
                foreach (var id in ids)
                {
                    if (_seen.Add(id))
                    {
                        _ids.Add(id);
                    }
                }
            }

            public PropertyValidationOutcome? ToOutcome()
                => _ids.Count > 0 ? new PropertyValidationOutcome(_ids) : null;
        }
    }
}
