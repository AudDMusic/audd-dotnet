using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudD.Internal;

/// <summary>
/// Custom converter for <see cref="StreamCallbackMatch"/>. Reads the inner
/// <c>result</c> object: splits <c>results[]</c> into <see cref="StreamCallbackMatch.Song"/>
/// (index 0) and <see cref="StreamCallbackMatch.Alternatives"/> (the rest), and
/// captures unknown top-level keys into <see cref="StreamCallbackMatch.Extras"/>.
///
/// <para>Mirror of audd-go's <c>parseMatch</c> helper. The split-on-index-zero is the
/// load-bearing piece — the SDK promises <see cref="StreamCallbackMatch.Song"/> is the
/// top match, regardless of how many candidates the server returned.</para>
/// </summary>
internal sealed class StreamCallbackMatchJsonConverter : JsonConverter<StreamCallbackMatch>
{
    private static readonly HashSet<string> KnownKeys = new(StringComparer.Ordinal)
    {
        "radio_id", "timestamp", "play_length", "results",
    };

    /// <summary>
    /// Expected shape per known <see cref="StreamCallbackSong"/> field, used to
    /// coerce wrong-typed scalars (e.g. <c>score:"85"</c> → <c>85</c>,
    /// <c>artist:123</c> → <c>"123"</c>) via the same policy as the standard-endpoint
    /// lenient parser, rather than dropping the whole candidate.
    /// </summary>
    private static readonly Dictionary<string, TolerantParser.Expect> SongKnownProps = new(StringComparer.Ordinal)
    {
        ["artist"] = TolerantParser.Expect.String,
        ["title"] = TolerantParser.Expect.String,
        ["score"] = TolerantParser.Expect.Number,
        ["album"] = TolerantParser.Expect.String,
        ["release_date"] = TolerantParser.Expect.String,
        ["label"] = TolerantParser.Expect.String,
        ["song_link"] = TolerantParser.Expect.String,
        ["isrc"] = TolerantParser.Expect.String,
        ["upc"] = TolerantParser.Expect.String,
        ["apple_music"] = TolerantParser.Expect.Object,
        ["spotify"] = TolerantParser.Expect.Object,
        ["deezer"] = TolerantParser.Expect.Object,
        ["napster"] = TolerantParser.Expect.Object,
        ["musicbrainz"] = TolerantParser.Expect.Array,
    };

    public override StreamCallbackMatch Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected object for StreamCallbackMatch");

        long? radioId = null;
        string? timestamp = null;
        long? playLength = null;
        var songs = new List<StreamCallbackSong>();
        var extras = new Dictionary<string, JsonElement>();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) break;
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("Expected property name");
            var name = reader.GetString() ?? "";
            reader.Read();
            switch (name)
            {
                case "radio_id":
                    // A successful callback must never fail to parse on a missing or
                    // wrong-typed radio_id: coerce a convertible value (e.g. "7" → 7),
                    // else leave it null rather than throwing.
                    radioId = CoerceInt64(ref reader);
                    break;
                case "timestamp":
                    timestamp = CoerceString(ref reader);
                    break;
                case "play_length":
                    playLength = CoerceInt64(ref reader);
                    break;
                case "results":
                    if (reader.TokenType == JsonTokenType.StartArray)
                    {
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                        {
                            using var doc = JsonDocument.ParseValue(ref reader);
                            // Skip a non-object candidate (e.g. a bare string) rather
                            // than surfacing an empty song for it.
                            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                                continue;
                            // Coerce wrong-typed candidate fields (score:"85" → 85,
                            // artist:123 → "123") the same way the standard-endpoint
                            // parser does, degrading only un-coercible fields to null —
                            // rather than dropping the whole candidate.
                            var s = TolerantParser.ParseObject(
                                doc.RootElement,
                                AudDJsonContext.Default.StreamCallbackSong,
                                SongKnownProps,
                                static () => new StreamCallbackSong());
                            songs.Add(s);
                        }
                    }
                    else if (reader.TokenType == JsonTokenType.Null)
                    {
                        // empty
                    }
                    else
                    {
                        throw new JsonException("Expected array for results");
                    }
                    break;
                default:
                    if (!KnownKeys.Contains(name))
                    {
                        using var doc = JsonDocument.ParseValue(ref reader);
                        extras[name] = doc.RootElement.Clone();
                    }
                    else
                    {
                        // Skip an unexpected known-name token (e.g. wrong type).
                        using var _ = JsonDocument.ParseValue(ref reader);
                    }
                    break;
            }
        }

        // A successful recognition callback must never throw on parse. When the
        // server returned no usable candidates, surface an empty default Song
        // and an empty Alternatives list rather than throwing.
        var song = songs.Count > 0 ? songs[0] : new StreamCallbackSong();
        var alternatives = songs.Count > 1
            ? songs.GetRange(1, songs.Count - 1)
            : new List<StreamCallbackSong>();

        return new StreamCallbackMatch
        {
            RadioId = radioId,
            Timestamp = timestamp,
            PlayLength = playLength,
            Song = song,
            Alternatives = alternatives,
            Extras = extras,
        };
    }

    /// <summary>
    /// Read the current value and coerce it to an Int64 per the family policy
    /// (number → truncate toward zero, numeric string → parse, bool → 0/1);
    /// returns null for nulls and un-coercible shapes — never throws.
    /// </summary>
    private static long? CoerceInt64(ref Utf8JsonReader reader)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        return TolerantParser.CoerceToInt64(doc.RootElement);
    }

    /// <summary>
    /// Read the current value and coerce it to a string per the family policy
    /// (string passes through, number → raw token text, bool → "true"/"false");
    /// object/array/null → null.
    /// </summary>
    private static string? CoerceString(ref Utf8JsonReader reader)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        return TolerantParser.CoerceToString(doc.RootElement);
    }

    public override void Write(Utf8JsonWriter writer, StreamCallbackMatch value, JsonSerializerOptions options)
    {
        // Re-emit on the wire shape: {radio_id, timestamp, play_length, results: [Song, ...Alternatives]}.
        writer.WriteStartObject();
        if (value.RadioId.HasValue) writer.WriteNumber("radio_id", value.RadioId.Value);
        if (value.Timestamp is not null) writer.WriteString("timestamp", value.Timestamp);
        if (value.PlayLength.HasValue) writer.WriteNumber("play_length", value.PlayLength.Value);
        writer.WritePropertyName("results");
        writer.WriteStartArray();
        JsonSerializer.Serialize(writer, value.Song, AudDJsonContext.Default.StreamCallbackSong);
        foreach (var alt in value.Alternatives)
        {
            JsonSerializer.Serialize(writer, alt, AudDJsonContext.Default.StreamCallbackSong);
        }
        writer.WriteEndArray();
        foreach (var kv in value.Extras)
        {
            writer.WritePropertyName(kv.Key);
            kv.Value.WriteTo(writer);
        }
        writer.WriteEndObject();
    }
}
