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
                    // wrong-typed radio_id: leave it null rather than throwing.
                    radioId = TryReadInt64(ref reader);
                    break;
                case "timestamp":
                    timestamp = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                    break;
                case "play_length":
                    playLength = TryReadInt64(ref reader);
                    break;
                case "results":
                    if (reader.TokenType == JsonTokenType.StartArray)
                    {
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                        {
                            using var doc = JsonDocument.ParseValue(ref reader);
                            // Skip a malformed candidate (wrong-typed field) rather than
                            // failing the whole callback parse.
                            try
                            {
                                var s = doc.RootElement.Deserialize(AudDJsonContext.Default.StreamCallbackSong);
                                if (s is not null) songs.Add(s);
                            }
                            catch (JsonException)
                            {
                                // ignore this candidate
                            }
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
    /// Read the current value as an Int64 when it is a JSON number that fits;
    /// returns null for nulls, strings, or any other shape — never throws.
    /// </summary>
    private static long? TryReadInt64(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var n))
            return n;
        return null;
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
