using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace AudD.Internal;

/// <summary>
/// Lenient response parsing for wire-boundary records. A successful response must
/// never fail to surface because one field arrived with the wrong JSON type: the
/// parser coerces a wrong-typed scalar to its target shape when the value is
/// convertible (e.g. <c>"85"</c> → <c>85</c>, <c>123</c> → <c>"123"</c>), and only
/// degrades the field to null when it is genuinely not convertible — rather than
/// throwing the whole object away.
///
/// <para>Strategy: try the strict source-generated deserialize first (the common
/// path, zero overhead). Only when that throws do we sanitize — rebuild the object,
/// coercing any known property whose JSON kind can't map to its target (dropping it
/// when coercion is not possible) — and retry. Unknown properties are preserved
/// (they flow to <c>Extras</c>). If even the sanitized parse fails, fall back to a
/// caller-supplied default.</para>
/// </summary>
internal static class TolerantParser
{
    /// <summary>
    /// The target shape a known property is coerced toward during sanitization.
    /// When the actual JSON kind already matches, the value passes through
    /// untouched; otherwise it is coerced per the family coercion policy (or
    /// dropped when not convertible).
    /// </summary>
    internal enum Expect
    {
        /// <summary>String value (or null).</summary>
        String,
        /// <summary>Integer value (or null). Numeric target backed by a CLR integer type (int/long).</summary>
        Number,
        /// <summary>Floating-point value (or null). Numeric target backed by a CLR double/float.</summary>
        Float,
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
                    if (prop.Value.ValueKind == JsonValueKind.Null || Matches(expect, prop.Value))
                    {
                        prop.WriteTo(w);
                    }
                    else if (TryCoerce(prop.Value, expect, out var coerced))
                    {
                        // Coerce a convertible wrong-typed scalar to its target shape.
                        w.WritePropertyName(prop.Name);
                        coerced(w);
                    }
                    // else: drop the un-coercible known field (reads as null/absent).
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

    private static bool Matches(Expect expect, JsonElement value)
    {
        var kind = value.ValueKind;
        return expect switch
        {
            Expect.String => kind == JsonValueKind.String,
            // An int target only "matches" a number the strict pass will accept
            // (an integer that fits Int64). A fractional/oversized number falls
            // through to coercion (truncation) rather than failing the retry.
            Expect.Number => kind == JsonValueKind.Number && value.TryGetInt64(out _),
            Expect.Float => kind == JsonValueKind.Number,
            Expect.Bool => kind == JsonValueKind.True || kind == JsonValueKind.False,
            Expect.Object => kind == JsonValueKind.Object,
            Expect.Array => kind == JsonValueKind.Array,
            _ => true,
        };
    }

    /// <summary>
    /// Attempt to coerce a wrong-typed scalar toward <paramref name="expect"/>. On
    /// success, <paramref name="write"/> emits the coerced JSON value into a writer.
    /// Returns false (and the field is dropped) when the value is not convertible.
    /// </summary>
    private static bool TryCoerce(JsonElement value, Expect expect, out Action<Utf8JsonWriter> write)
    {
        write = null!;
        switch (expect)
        {
            case Expect.String:
            {
                var s = CoerceToString(value);
                if (s is null) return false;
                write = w => w.WriteStringValue(s);
                return true;
            }
            case Expect.Number:
            {
                var n = CoerceToInt64(value);
                if (n is null) return false;
                write = w => w.WriteNumberValue(n.Value);
                return true;
            }
            case Expect.Float:
            {
                var d = CoerceToDouble(value);
                if (d is null) return false;
                write = w => w.WriteNumberValue(d.Value);
                return true;
            }
            case Expect.Bool:
            {
                var b = CoerceToBool(value);
                if (b is null) return false;
                write = w => w.WriteBooleanValue(b.Value);
                return true;
            }
            default:
                // Object/Array targets keep their existing degrade behavior.
                return false;
        }
    }

    // ---- Coercion primitives (family policy; invariant culture) ----

    /// <summary>
    /// number → its raw JSON token text (85 → "85", 8.5 → "8.5"); bool →
    /// "true"/"false"; object/array/null → null.
    /// </summary>
    internal static string? CoerceToString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null,
    };

    /// <summary>
    /// float → truncate toward zero; numeric string → parse (full-string,
    /// invariant, "7.9" → 7); bool → 0/1; anything else → null.
    /// </summary>
    internal static long? CoerceToInt64(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                if (value.TryGetInt64(out var i)) return i;
                if (value.TryGetDouble(out var dn) && IsFinite(dn)) return (long)dn;
                return null;
            case JsonValueKind.String:
                var d = ParseNumericString(value.GetString());
                return d is null ? null : (long)d.Value;
            case JsonValueKind.True: return 1;
            case JsonValueKind.False: return 0;
            default: return null;
        }
    }

    /// <summary>
    /// int → convert; numeric string → parse (full-string, invariant); anything
    /// else (bool/object/array/non-numeric string) → null.
    /// </summary>
    internal static double? CoerceToDouble(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                return value.TryGetDouble(out var d) && IsFinite(d) ? d : (double?)null;
            case JsonValueKind.String:
                return ParseNumericString(value.GetString());
            default:
                return null;
        }
    }

    /// <summary>
    /// number → (v != 0); string → case-insensitive/trimmed whitelist:
    /// true/1/yes/on → true, false/0/no/off/"" → false, any other string → null;
    /// object/array → null.
    /// </summary>
    internal static bool? CoerceToBool(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.Number:
                if (value.TryGetDouble(out var d)) return d != 0;
                return null;
            case JsonValueKind.String:
                var s = value.GetString();
                if (s is null) return null;
                s = s.Trim().ToLowerInvariant();
                return s switch
                {
                    "true" or "1" or "yes" or "on" => true,
                    "false" or "0" or "no" or "off" or "" => false,
                    _ => (bool?)null,
                };
            default:
                return null;
        }
    }

    /// <summary>
    /// Full-string invariant-culture numeric parse. Rejects partial matches,
    /// empty/whitespace, and NaN/Infinity — degrading to null.
    /// </summary>
    private static double? ParseNumericString(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (!double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return null;
        return IsFinite(d) ? d : (double?)null;
    }

    private static bool IsFinite(double d) => !double.IsNaN(d) && !double.IsInfinity(d);
}
