using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace AudD.Internal;

/// <summary>
/// Lenient response parsing for wire-boundary records. A successful response must
/// never fail to surface because one field arrived with the wrong JSON type: the
/// parser degrades per-field (dropping a wrong-typed value so it reads as
/// null/0/absent) rather than throwing the whole object away.
///
/// <para>Strategy: try the strict source-generated deserialize first (the common
/// path, zero overhead). Only when that throws do we sanitize — rebuild the object
/// dropping any known property whose JSON kind can't map to its target — and retry.
/// Unknown properties are preserved (they flow to <c>Extras</c>). If even the
/// sanitized parse fails, fall back to a caller-supplied default.</para>
/// </summary>
internal static class TolerantParser
{
    /// <summary>
    /// Which JSON kinds a known property tolerates. A property is dropped during
    /// sanitization when its actual kind is not in this set (Null is always fine —
    /// nullable targets accept it, and dropping never hurts).
    /// </summary>
    internal enum Expect
    {
        /// <summary>String value (or null).</summary>
        String,
        /// <summary>Numeric value (or null). Fractional/oversized numbers are dropped only if the strict pass rejects them.</summary>
        Number,
        /// <summary>Boolean value (or null).</summary>
        Bool,
        /// <summary>Object value (or null).</summary>
        Object,
        /// <summary>Array value (or null).</summary>
        Array,
    }

    public static T ParseObject<T>(
        JsonElement element,
        JsonTypeInfo<T> typeInfo,
        IReadOnlyDictionary<string, Expect> knownProps,
        Func<T> fallback)
        where T : class
    {
        // Fast path: strict deserialize. Most responses parse here untouched.
        try
        {
            return element.Deserialize(typeInfo) ?? fallback();
        }
        catch (JsonException)
        {
            // Fall through to sanitize + retry.
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return fallback();
        }

        try
        {
            var sanitized = Sanitize(element, knownProps);
            return sanitized.Deserialize(typeInfo) ?? fallback();
        }
        catch (JsonException)
        {
            return fallback();
        }
    }

    private static JsonElement Sanitize(JsonElement element, IReadOnlyDictionary<string, Expect> knownProps)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            foreach (var prop in element.EnumerateObject())
            {
                if (knownProps.TryGetValue(prop.Name, out var expect))
                {
                    var kind = prop.Value.ValueKind;
                    if (kind == JsonValueKind.Null || Matches(expect, kind))
                    {
                        prop.WriteTo(w);
                    }
                    // else: drop the wrong-typed known field (reads as null/absent).
                }
                else
                {
                    // Unknown property — keep it verbatim (flows to Extras).
                    prop.WriteTo(w);
                }
            }
            w.WriteEndObject();
        }
        ms.Position = 0;
        using var doc = JsonDocument.Parse(ms);
        return doc.RootElement.Clone();
    }

    private static bool Matches(Expect expect, JsonValueKind kind) => expect switch
    {
        Expect.String => kind == JsonValueKind.String,
        Expect.Number => kind == JsonValueKind.Number,
        Expect.Bool => kind == JsonValueKind.True || kind == JsonValueKind.False,
        Expect.Object => kind == JsonValueKind.Object,
        Expect.Array => kind == JsonValueKind.Array,
        _ => true,
    };
}
