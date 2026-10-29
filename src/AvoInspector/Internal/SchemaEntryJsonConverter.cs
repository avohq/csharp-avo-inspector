using System;
using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Avo.Inspector.Internal
{
    /// <summary>
    /// Serializes a <see cref="SchemaEntry"/> to the exact Inspector wire shape (SPEC.md §7.3.4):
    /// <c>{ "propertyName", "propertyType", "encryptedPropertyValue"?, "children"?, "failedEventIds"? }</c>
    /// with each optional key omitted when its value is <c>null</c>. The two wire-only keys are only
    /// ever set on the copy of the schema sent in dev and staging, so a schema without them
    /// serializes exactly as in 1.1.x. The heterogeneous <c>children</c> union
    /// (type strings, nested <see cref="SchemaEntry"/> objects, nested arrays) is written
    /// recursively. This converter is write-only; deserialization is not supported.
    /// </summary>
    internal sealed class SchemaEntryJsonConverter : JsonConverter<SchemaEntry>
    {
        public override SchemaEntry Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => throw new NotSupportedException("SchemaEntry is serialize-only.");

        public override void Write(Utf8JsonWriter writer, SchemaEntry value, JsonSerializerOptions options)
            => WriteEntry(writer, value);

        private static void WriteEntry(Utf8JsonWriter writer, SchemaEntry entry)
        {
            writer.WriteStartObject();
            writer.WriteString("propertyName", entry.PropertyName);
            writer.WriteString("propertyType", entry.PropertyType);
            if (entry.EncryptedPropertyValue != null)
            {
                writer.WriteString("encryptedPropertyValue", entry.EncryptedPropertyValue);
            }
            if (entry.Children != null)
            {
                writer.WritePropertyName("children");
                WriteValue(writer, entry.Children);
            }
            if (entry.FailedEventIds != null)
            {
                writer.WriteStartArray("failedEventIds");
                foreach (var id in entry.FailedEventIds)
                {
                    writer.WriteStringValue(id);
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }

        /// <summary>
        /// Writes a single <c>children</c> union member: a type string, a nested
        /// <see cref="SchemaEntry"/>, or a (possibly nested) array of those. Order matters: a
        /// <see cref="string"/> is an <see cref="IEnumerable"/> of chars, so it is checked before
        /// the generic array branch.
        /// </summary>
        private static void WriteValue(Utf8JsonWriter writer, object? value)
        {
            switch (value)
            {
                case null:
                    writer.WriteNullValue();
                    return;
                case string s:
                    writer.WriteStringValue(s);
                    return;
                case SchemaEntry entry:
                    WriteEntry(writer, entry);
                    return;
                case IEnumerable list:
                    writer.WriteStartArray();
                    foreach (var item in list)
                    {
                        WriteValue(writer, item);
                    }
                    writer.WriteEndArray();
                    return;
                default:
                    // Unreachable for well-formed schema output; emit a string for safety.
                    writer.WriteStringValue(value.ToString());
                    return;
            }
        }
    }
}
