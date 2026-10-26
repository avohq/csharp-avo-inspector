using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Avo.Inspector.Internal
{
    /// <summary>
    /// Reads raw event property values the way the JavaScript SDKs see them, for event spec
    /// validation and value encryption: which values are strings or numbers, the string a value is
    /// compared as (JavaScript's <c>String(value)</c>), and its JSON (<c>JSON.stringify(value)</c>).
    /// Values are classified the same way <see cref="AvoSchemaParser"/> classifies them, so a value
    /// reported as <c>"string"</c> is a string here too.
    /// </summary>
    /// <remarks>
    /// JSON is written by hand with <see cref="Utf8JsonWriter"/>, never with the reflection-based
    /// serializer, so it works in trimmed and Native AOT host applications. A type with no JSON
    /// equivalent (an enum, a custom class) is written as its <c>ToString()</c>, since its members
    /// cannot be read without reflection.
    /// </remarks>
    internal static class RawValues
    {
        // Bounds on JSON output: deeper or larger values (including a value that contains itself)
        // throw instead of recursing without end. Callers treat the throw as "no JSON".
        private const int MaxJsonDepth = 10;
        private const int MaxJsonNodes = 10_000;

        /// <summary>A map that <see cref="AvoSchemaParser"/> reports as <c>"object"</c>.</summary>
        public static bool IsMap(object? value) => AvoSchemaParser.IsObjectMap(value);

        /// <summary>A collection that <see cref="AvoSchemaParser"/> reports as a list.</summary>
        public static bool IsList(object? value) => value is IEnumerable && !(value is string) && !IsMap(value);

        /// <summary>The value of a map's property, or false when the value is not a map or has no such key.</summary>
        public static bool TryGetChild(object? container, string name, out object? value)
        {
            value = null;
            if (container is IDictionary<string, object?> typed)
            {
                return typed.TryGetValue(name, out value);
            }
            if (container is IDictionary raw)
            {
                foreach (DictionaryEntry entry in raw)
                {
                    if ((entry.Key?.ToString() ?? string.Empty) == name)
                    {
                        value = entry.Value;
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>The value as a string when the schema reports it as <c>"string"</c>.</summary>
        public static bool TryGetString(object? value, out string text)
        {
            switch (value)
            {
                case string s:
                    text = s;
                    return true;
                case char c:
                    text = c.ToString();
                    return true;
                case Guid g:
                    text = g.ToString("D");
                    return true;
                case DateTime dt:
                    text = dt.ToString("O", CultureInfo.InvariantCulture);
                    return true;
                case DateTimeOffset dto:
                    text = dto.ToString("O", CultureInfo.InvariantCulture);
                    return true;
                case TimeSpan ts:
                    text = ts.ToString("c", CultureInfo.InvariantCulture);
                    return true;
                case Uri uri:
                    text = uri.OriginalString;
                    return true;
#if NET6_0_OR_GREATER
                case DateOnly d:
                    text = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    return true;
                case TimeOnly t:
                    text = t.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture);
                    return true;
#endif
                default:
                    text = string.Empty;
                    return false;
            }
        }

        /// <summary>The value as a number when the schema reports it as <c>"int"</c> or <c>"float"</c>.</summary>
        public static bool TryGetNumber(object? value, out double number)
        {
            switch (value)
            {
                case sbyte v: number = v; return true;
                case byte v: number = v; return true;
                case short v: number = v; return true;
                case ushort v: number = v; return true;
                case int v: number = v; return true;
                case uint v: number = v; return true;
                case long v: number = v; return true;
                case ulong v: number = v; return true;
                case float v: number = v; return true;
                case double v: number = v; return true;
                case decimal v: number = (double)v; return true;
                default:
                    number = double.NaN;
                    return false;
            }
        }

        /// <summary>
        /// The string a value is compared as against pinned and allowed values: what JavaScript's
        /// <c>String()</c> gives for the same JSON value — <c>"true"</c>, <c>"null"</c>, <c>"1.5"</c>,
        /// <c>"1"</c> for <c>1.0</c> — and JSON for a map or list. A value whose JSON cannot be
        /// written falls back to <c>"[object Object]"</c>, as in JavaScript.
        /// </summary>
        public static string ComparableString(object? value)
        {
            if (value == null)
            {
                return "null";
            }
            if (value is bool b)
            {
                return b ? "true" : "false";
            }
            if (TryGetString(value, out var text))
            {
                return text;
            }
            if (value is float f)
            {
                // A float keeps its own shortest form: 0.1f is "0.1", not 0.10000000149011612.
                return float.IsNaN(f) || float.IsInfinity(f)
                    ? FormatNumber(f)
                    : FormatNumber(double.Parse(f.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));
            }
            if (value is decimal m)
            {
                return FormatDecimal(m);
            }
            if (value is ulong ul)
            {
                return ul.ToString(CultureInfo.InvariantCulture);
            }
            if (value is long l)
            {
                return l.ToString(CultureInfo.InvariantCulture);
            }
            if (TryGetNumber(value, out var number))
            {
                return FormatNumber(number);
            }
            if (IsMap(value) || IsList(value))
            {
                try
                {
                    return ToJson(value);
                }
                catch (Exception)
                {
                    return "[object Object]";
                }
            }
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        /// <summary>
        /// The value's compact JSON, as <c>JSON.stringify</c> writes the same data: maps as objects
        /// in their enumeration order, lists as arrays, <c>NaN</c> and infinities as <c>null</c>.
        /// Throws on a value nested deeper than 10 levels or with more than 10,000 nodes.
        /// </summary>
        public static string ToJson(object? value)
        {
            using (var stream = new MemoryStream())
            {
                using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { SkipValidation = true }))
                {
                    var nodes = 0;
                    WriteJson(writer, value, 0, ref nodes);
                }
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        private static void WriteJson(Utf8JsonWriter writer, object? value, int depth, ref int nodes)
        {
            if (++nodes > MaxJsonNodes)
            {
                throw new InvalidOperationException("Value too large to serialize.");
            }
            if (value == null)
            {
                writer.WriteNullValue();
                return;
            }
            if (value is bool b)
            {
                writer.WriteBooleanValue(b);
                return;
            }
            if (TryGetString(value, out var text))
            {
                writer.WriteStringValue(text);
                return;
            }
            if (TryGetNumber(value, out var number))
            {
                if (double.IsNaN(number) || double.IsInfinity(number))
                {
                    writer.WriteNullValue();
                }
                else
                {
                    writer.WriteRawValue(ComparableString(value), skipInputValidation: true);
                }
                return;
            }
            if (IsMap(value))
            {
                if (depth >= MaxJsonDepth)
                {
                    throw new InvalidOperationException("Value nested too deeply to serialize.");
                }
                writer.WriteStartObject();
                foreach (var entry in AvoSchemaParser.EnumerateMap(value))
                {
                    writer.WritePropertyName(entry.Key);
                    WriteJson(writer, entry.Value, depth + 1, ref nodes);
                }
                writer.WriteEndObject();
                return;
            }
            if (value is IEnumerable list)
            {
                if (depth >= MaxJsonDepth)
                {
                    throw new InvalidOperationException("Value nested too deeply to serialize.");
                }
                writer.WriteStartArray();
                foreach (var item in list)
                {
                    WriteJson(writer, item, depth + 1, ref nodes);
                }
                writer.WriteEndArray();
                return;
            }
            writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
        }

        /// <summary>
        /// Formats a double as JavaScript's <c>Number.prototype.toString()</c> does: the shortest
        /// digits that round-trip, plain notation for exponents from -7 to 20, exponent notation
        /// (<c>1e+21</c>, <c>1e-7</c>) outside it, and <c>NaN</c>, <c>Infinity</c>, <c>-Infinity</c>.
        /// </summary>
        internal static string FormatNumber(double value)
        {
            if (double.IsNaN(value))
            {
                return "NaN";
            }
            if (double.IsPositiveInfinity(value))
            {
                return "Infinity";
            }
            if (double.IsNegativeInfinity(value))
            {
                return "-Infinity";
            }
            if (value == 0)
            {
                return "0";
            }

            // "R" gives the shortest round-trip digits; split it into a digit string and the
            // position n of the decimal point relative to those digits (value = 0.digits × 10^n).
            var r = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
            var exponent = 0;
            var e = r.IndexOfAny(new[] { 'E', 'e' });
            if (e >= 0)
            {
                exponent = int.Parse(r.Substring(e + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                r = r.Substring(0, e);
            }
            var point = r.IndexOf('.');
            var digits = point >= 0 ? r.Remove(point, 1) : r;
            var n = (point >= 0 ? point : r.Length) + exponent;
            var trimmed = digits.TrimStart('0');
            n -= digits.Length - trimmed.Length;
            digits = trimmed.TrimEnd('0');
            var k = digits.Length;

            string result;
            if (k <= n && n <= 21)
            {
                result = digits + new string('0', n - k);
            }
            else if (0 < n && n <= 21)
            {
                result = digits.Substring(0, n) + "." + digits.Substring(n);
            }
            else if (-6 < n && n <= 0)
            {
                result = "0." + new string('0', -n) + digits;
            }
            else
            {
                var exp = n - 1;
                var mantissa = k == 1 ? digits : digits.Substring(0, 1) + "." + digits.Substring(1);
                result = mantissa + "e" + (exp >= 0 ? "+" : "-") + Math.Abs(exp).ToString(CultureInfo.InvariantCulture);
            }
            return value < 0 ? "-" + result : result;
        }

        // A decimal written without the trailing zeros of its scale (1.50m is "1.5", 2.0m is "2"),
        // so it compares like the same JSON number.
        private static string FormatDecimal(decimal value)
        {
            var text = value.ToString(CultureInfo.InvariantCulture);
            if (text.IndexOf('.') >= 0)
            {
                text = text.TrimEnd('0').TrimEnd('.');
            }
            return text == "-0" ? "0" : text;
        }
    }
}
