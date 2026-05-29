using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace AudD;

/// <summary>
/// Streaming providers reachable through the lis.tn redirect helper, or
/// directly via the corresponding metadata block.
/// </summary>
public enum StreamingProvider
{
    /// <summary>Spotify — direct via <c>spotify.external_urls["spotify"]</c>, else lis.tn redirect.</summary>
    Spotify,

    /// <summary>Apple Music — direct via <c>apple_music.url</c>, else lis.tn redirect.</summary>
    AppleMusic,

    /// <summary>Deezer — direct via <c>deezer.link</c>, else lis.tn redirect.</summary>
    Deezer,

    /// <summary>Napster — direct via <c>napster.href</c> (extras), else lis.tn redirect.</summary>
    Napster,

    /// <summary>YouTube — only the lis.tn redirect path; no metadata block.</summary>
    YouTube,
}

internal static class StreamingProviders
{
    /// <summary>All providers in canonical order.</summary>
    public static readonly StreamingProvider[] All =
    {
        StreamingProvider.Spotify,
        StreamingProvider.AppleMusic,
        StreamingProvider.Deezer,
        StreamingProvider.Napster,
        StreamingProvider.YouTube,
    };

    /// <summary>The lis.tn query token (e.g. <c>"apple_music"</c>) for a provider.</summary>
    public static string ToQueryToken(StreamingProvider p) => p switch
    {
        StreamingProvider.Spotify => "spotify",
        StreamingProvider.AppleMusic => "apple_music",
        StreamingProvider.Deezer => "deezer",
        StreamingProvider.Napster => "napster",
        StreamingProvider.YouTube => "youtube",
        _ => throw new ArgumentOutOfRangeException(nameof(p), p, "Unknown StreamingProvider"),
    };

    /// <summary>
    /// Returns <c>"{songLink}?{provider}"</c> when <paramref name="songLink"/> is a
    /// lis.tn URL; otherwise null.
    /// </summary>
    public static string? LisTnRedirect(string? songLink, string providerToken)
    {
        if (string.IsNullOrEmpty(songLink)) return null;
        if (!Uri.TryCreate(songLink, UriKind.Absolute, out var uri)) return null;
        if (!string.Equals(uri.Host, "lis.tn", StringComparison.OrdinalIgnoreCase)) return null;
        var sep = string.IsNullOrEmpty(uri.Query) ? "?" : "&";
        return $"{songLink}{sep}{providerToken}";
    }

    /// <summary>Read a string-valued <c>JsonElement</c> from an Extras dict, or return null.</summary>
    public static string? ExtrasString(IReadOnlyDictionary<string, JsonElement>? extras, string key)
    {
        if (extras is null || !extras.TryGetValue(key, out var el)) return null;
        if (el.ValueKind != JsonValueKind.String) return null;
        var s = el.GetString();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    /// <summary>Pull <c>obj.{key}</c> as a string when <paramref name="extras"/>[<paramref name="objKey"/>] is an object.</summary>
    public static string? ExtrasNestedString(IReadOnlyDictionary<string, JsonElement>? extras, string objKey, string innerKey)
    {
        if (extras is null || !extras.TryGetValue(objKey, out var el)) return null;
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(innerKey, out var inner)) return null;
        if (inner.ValueKind != JsonValueKind.String) return null;
        var s = inner.GetString();
        return string.IsNullOrEmpty(s) ? null : s;
    }
}

/// <summary>Apple Music metadata block on a recognition result.</summary>
public sealed record AppleMusicMetadata
{
    [JsonPropertyName("artistName")] public string? ArtistName { get; init; }
    [JsonPropertyName("url")] public string? Url { get; init; }
    [JsonPropertyName("durationInMillis")] public long? DurationInMillis { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("isrc")] public string? Isrc { get; init; }
    [JsonPropertyName("albumName")] public string? AlbumName { get; init; }
    [JsonPropertyName("trackNumber")] public int? TrackNumber { get; init; }
    [JsonPropertyName("composerName")] public string? ComposerName { get; init; }
    [JsonPropertyName("discNumber")] public int? DiscNumber { get; init; }
    [JsonPropertyName("releaseDate")] public string? ReleaseDate { get; init; }

    /// <summary>Forward-compat: any unknown server fields land here.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Extras { get; set; } = new();
}

/// <summary>Spotify metadata block on a recognition result.</summary>
public sealed record SpotifyMetadata
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("duration_ms")] public long? DurationMs { get; init; }
    [JsonPropertyName("explicit")] public bool? Explicit { get; init; }
    [JsonPropertyName("popularity")] public int? Popularity { get; init; }
    [JsonPropertyName("track_number")] public int? TrackNumber { get; init; }
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("uri")] public string? Uri { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> Extras { get; set; } = new();
}

/// <summary>Deezer metadata block on a recognition result.</summary>
public sealed record DeezerMetadata
{
    [JsonPropertyName("id")] public long? Id { get; init; }
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("duration")] public long? Duration { get; init; }
    [JsonPropertyName("link")] public string? Link { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> Extras { get; set; } = new();
}

/// <summary>Napster metadata block on a recognition result.</summary>
public sealed record NapsterMetadata
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("isrc")] public string? Isrc { get; init; }
    [JsonPropertyName("artistName")] public string? ArtistName { get; init; }
    [JsonPropertyName("albumName")] public string? AlbumName { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> Extras { get; set; } = new();
}

/// <summary>One MusicBrainz entry from a recognition result.</summary>
public sealed record MusicBrainzEntry
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("length")] public long? Length { get; init; }

    /// <summary>Server returns either int or string. Stored verbatim.</summary>
    [JsonPropertyName("score")] public JsonElement? Score { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> Extras { get; set; } = new();
}

/// <summary>
/// Recognition result from the standard endpoint. Single type covering both
/// public-catalog and custom-catalog matches — see spec §5.1.
/// </summary>
public sealed record RecognitionResult
{
    /// <summary>Always present on a match (e.g. "00:56").</summary>
    [JsonPropertyName("timecode")] public string Timecode { get; init; } = "";

    /// <summary>Set for custom-catalog matches.</summary>
    [JsonPropertyName("audio_id")] public long? AudioId { get; init; }

    [JsonPropertyName("artist")] public string? Artist { get; init; }
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("album")] public string? Album { get; init; }
    [JsonPropertyName("release_date")] public string? ReleaseDate { get; init; }
    [JsonPropertyName("label")] public string? Label { get; init; }
    [JsonPropertyName("song_link")] public string? SongLink { get; init; }
    [JsonPropertyName("isrc")] public string? Isrc { get; init; }
    [JsonPropertyName("upc")] public string? Upc { get; init; }
    [JsonPropertyName("apple_music")] public AppleMusicMetadata? AppleMusic { get; init; }
    [JsonPropertyName("spotify")] public SpotifyMetadata? Spotify { get; init; }
    [JsonPropertyName("deezer")] public DeezerMetadata? Deezer { get; init; }
    [JsonPropertyName("napster")] public NapsterMetadata? Napster { get; init; }
    [JsonPropertyName("musicbrainz")] public IReadOnlyList<MusicBrainzEntry>? MusicBrainz { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> Extras { get; set; } = new();

    /// <summary>Full unparsed payload — set by the SDK after parse.</summary>
    [JsonIgnore]
    public JsonElement RawResponse { get; init; }

    /// <summary>True when <see cref="AudioId"/> is set (custom-catalog match).</summary>
    [JsonIgnore] public bool IsCustomMatch => AudioId.HasValue;

    /// <summary>True when this looks like a public-catalog match (artist/title set, no <see cref="AudioId"/>).</summary>
    [JsonIgnore] public bool IsPublicMatch => !AudioId.HasValue && (!string.IsNullOrEmpty(Artist) || !string.IsNullOrEmpty(Title));

    /// <summary>
    /// Cover-art URL for <c>lis.tn</c>-hosted song_links: appends <c>?thumb</c> (or
    /// <c>&amp;thumb</c>). Returns null for YouTube and other hosts, and for
    /// custom-DB matches (which have no <see cref="SongLink"/>).
    /// </summary>
    [JsonIgnore]
    public string? ThumbnailUrl => StreamingProviders.LisTnRedirect(SongLink, "thumb");

    /// <summary>
    /// Direct or redirect URL for a streaming provider.
    ///
    /// <para>Resolution order:</para>
    /// <list type="number">
    /// <item><description>
    ///   Direct URL from the metadata block when present:
    ///   <see cref="AppleMusicMetadata.Url"/>,
    ///   <c>spotify.external_urls["spotify"]</c> (read from <see cref="SpotifyMetadata.Extras"/>),
    ///   <see cref="DeezerMetadata.Link"/>,
    ///   or <c>napster.href</c> (read from <see cref="NapsterMetadata.Extras"/>).
    ///   Direct URLs avoid a redirect hop.
    /// </description></item>
    /// <item><description>
    ///   lis.tn redirect <c>{song_link}?{provider}</c> when <see cref="SongLink"/> is a lis.tn URL.
    /// </description></item>
    /// <item><description>
    ///   <c>null</c> when neither path resolves.
    /// </description></item>
    /// </list>
    ///
    /// <para><see cref="StreamingProvider.YouTube"/> has only the lis.tn path —
    /// there is no per-track YouTube metadata block.</para>
    /// </summary>
    public string? StreamingUrl(StreamingProvider provider)
    {
        var direct = DirectStreamingUrl(provider);
        if (direct is not null) return direct;
        return StreamingProviders.LisTnRedirect(SongLink, StreamingProviders.ToQueryToken(provider));
    }

    private string? DirectStreamingUrl(StreamingProvider provider)
    {
        switch (provider)
        {
            case StreamingProvider.AppleMusic:
                if (!string.IsNullOrEmpty(AppleMusic?.Url)) return AppleMusic!.Url;
                break;
            case StreamingProvider.Spotify:
                if (Spotify is not null)
                {
                    var ext = StreamingProviders.ExtrasNestedString(Spotify.Extras, "external_urls", "spotify");
                    if (ext is not null) return ext;
                }
                break;
            case StreamingProvider.Deezer:
                if (!string.IsNullOrEmpty(Deezer?.Link)) return Deezer!.Link;
                break;
            case StreamingProvider.Napster:
                if (Napster is not null)
                {
                    var href = StreamingProviders.ExtrasString(Napster.Extras, "href");
                    if (href is not null) return href;
                }
                break;
            case StreamingProvider.YouTube:
                // No direct metadata block. lis.tn redirect only.
                break;
        }
        return null;
    }

    /// <summary>
    /// All providers with a resolvable URL — direct or via lis.tn redirect.
    /// Returns an empty dictionary when neither path resolves for any provider.
    /// </summary>
    public IReadOnlyDictionary<StreamingProvider, string> StreamingUrls()
    {
        var dict = new Dictionary<StreamingProvider, string>();
        foreach (var p in StreamingProviders.All)
        {
            var url = StreamingUrl(p);
            if (url is not null) dict[p] = url;
        }
        return dict;
    }

    /// <summary>
    /// First available 30-second audio preview URL, in priority order:
    /// <c>apple_music.previews[0].url</c> → <c>spotify.preview_url</c> → <c>deezer.preview</c>.
    /// Returns <c>null</c> when none of those preview fields are populated.
    ///
    /// <para><b>TOS caveat:</b> previews are governed by their respective providers'
    /// terms of use. SDK consumers are responsible for honoring those terms,
    /// including caching restrictions, attribution requirements, and any
    /// redistribution constraints.</para>
    /// </summary>
    public string? PreviewUrl()
    {
        if (AppleMusic is not null)
        {
            // previews lives in Extras: list of {"url": "..."}.
            if (AppleMusic.Extras.TryGetValue("previews", out var previews)
                && previews.ValueKind == JsonValueKind.Array
                && previews.GetArrayLength() > 0)
            {
                var first = previews[0];
                if (first.ValueKind == JsonValueKind.Object
                    && first.TryGetProperty("url", out var urlEl)
                    && urlEl.ValueKind == JsonValueKind.String)
                {
                    var s = urlEl.GetString();
                    if (!string.IsNullOrEmpty(s)) return s;
                }
            }
        }
        if (Spotify is not null)
        {
            var s = StreamingProviders.ExtrasString(Spotify.Extras, "preview_url");
            if (s is not null) return s;
        }
        if (Deezer is not null)
        {
            var s = StreamingProviders.ExtrasString(Deezer.Extras, "preview");
            if (s is not null) return s;
        }
        return null;
    }
}

/// <summary>One match from the enterprise endpoint.</summary>
public sealed record EnterpriseMatch
{
    /// <summary>
    /// Match score, when the server reported one. The enterprise endpoint
    /// legitimately omits <c>score</c> (along with <c>isrc</c>/<c>upc</c>/<c>label</c>)
    /// on some matches, so this is <c>null</c> rather than <c>0</c> when absent.
    /// </summary>
    [JsonPropertyName("score")] public int? Score { get; init; }
    [JsonPropertyName("timecode")] public string Timecode { get; init; } = "";
    [JsonPropertyName("artist")] public string? Artist { get; init; }
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("album")] public string? Album { get; init; }
    [JsonPropertyName("release_date")] public string? ReleaseDate { get; init; }
    [JsonPropertyName("label")] public string? Label { get; init; }
    [JsonPropertyName("isrc")] public string? Isrc { get; init; }
    [JsonPropertyName("upc")] public string? Upc { get; init; }
    [JsonPropertyName("song_link")] public string? SongLink { get; init; }
    [JsonPropertyName("start_offset")] public long? StartOffset { get; init; }
    [JsonPropertyName("end_offset")] public long? EndOffset { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> Extras { get; set; } = new();

    [JsonIgnore]
    public JsonElement RawResponse { get; init; }

    /// <summary>
    /// Cover-art URL for <c>lis.tn</c>-hosted song_links: appends <c>?thumb</c>
    /// (or <c>&amp;thumb</c>). Returns null otherwise.
    /// </summary>
    [JsonIgnore]
    public string? ThumbnailUrl => StreamingProviders.LisTnRedirect(SongLink, "thumb");

    /// <summary>
    /// lis.tn redirect URL for a streaming provider, or <c>null</c> when
    /// <see cref="SongLink"/> is not a lis.tn URL. Enterprise matches do not
    /// carry per-provider metadata blocks, so only the lis.tn redirect path
    /// applies here. See <see cref="RecognitionResult.StreamingUrl"/> for the
    /// direct-URL fallback used by standard recognition results.
    /// </summary>
    public string? StreamingUrl(StreamingProvider provider)
        => StreamingProviders.LisTnRedirect(SongLink, StreamingProviders.ToQueryToken(provider));

    /// <summary>
    /// All five providers' lis.tn redirect URLs, or empty when
    /// <see cref="SongLink"/> is not a lis.tn URL.
    /// </summary>
    public IReadOnlyDictionary<StreamingProvider, string> StreamingUrls()
    {
        var dict = new Dictionary<StreamingProvider, string>();
        foreach (var p in StreamingProviders.All)
        {
            var url = StreamingUrl(p);
            if (url is not null) dict[p] = url;
        }
        return dict;
    }
}

/// <summary>One chunk in an enterprise response.</summary>
public sealed record EnterpriseChunkResult
{
    [JsonPropertyName("songs")] public IReadOnlyList<EnterpriseMatch> Songs { get; init; } = Array.Empty<EnterpriseMatch>();
    [JsonPropertyName("offset")] public string? Offset { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> Extras { get; set; } = new();
}

/// <summary>One stream from <c>streams.list()</c>.</summary>
public sealed record Stream
{
    /// <summary>The radio_id for this stream, when present.</summary>
    [JsonPropertyName("radio_id")] public long? RadioId { get; init; }
    [JsonPropertyName("url")] public string Url { get; init; } = "";

    /// <summary>Whether the stream is running, when the server reported the flag.</summary>
    [JsonPropertyName("stream_running")] public bool? StreamRunning { get; init; }
    [JsonPropertyName("longpoll_category")] public string? LongpollCategory { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> Extras { get; set; } = new();
}

/// <summary>
/// One candidate song in a recognition match. Almost every match has exactly one
/// <see cref="StreamCallbackMatch.Song"/>; multiple candidates only appear when the
/// same fingerprint resolves to several near-identical catalog records.
/// </summary>
public sealed record StreamCallbackSong
{
    [JsonPropertyName("artist")] public string Artist { get; init; } = "";
    [JsonPropertyName("title")] public string Title { get; init; } = "";

    /// <summary>Match score, when the server reported one; <c>null</c> when absent.</summary>
    [JsonPropertyName("score")] public int? Score { get; init; }
    [JsonPropertyName("album")] public string? Album { get; init; }
    [JsonPropertyName("release_date")] public string? ReleaseDate { get; init; }
    [JsonPropertyName("label")] public string? Label { get; init; }
    [JsonPropertyName("song_link")] public string? SongLink { get; init; }
    [JsonPropertyName("isrc")] public string? Isrc { get; init; }
    [JsonPropertyName("upc")] public string? Upc { get; init; }
    [JsonPropertyName("apple_music")] public AppleMusicMetadata? AppleMusic { get; init; }
    [JsonPropertyName("spotify")] public SpotifyMetadata? Spotify { get; init; }
    [JsonPropertyName("deezer")] public DeezerMetadata? Deezer { get; init; }
    [JsonPropertyName("napster")] public NapsterMetadata? Napster { get; init; }
    [JsonPropertyName("musicbrainz")] public IReadOnlyList<MusicBrainzEntry>? MusicBrainz { get; init; }

    /// <summary>Forward-compat: any unknown server fields land here.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Extras { get; set; } = new();
}

/// <summary>
/// One recognition event from a stream callback or longpoll. Carries the top
/// match in <see cref="Song"/>; rare extra candidates live in
/// <see cref="Alternatives"/>.
///
/// <para>Read this with the <see cref="JsonConverter"/> attached to the type — direct
/// <c>JsonSerializer.Deserialize&lt;StreamCallbackMatch&gt;</c> on the inner
/// <c>result</c> object splits the <c>results[]</c> array into <see cref="Song"/>
/// (index 0) and <see cref="Alternatives"/> (the rest).</para>
/// </summary>
[JsonConverter(typeof(Internal.StreamCallbackMatchJsonConverter))]
public sealed record StreamCallbackMatch
{
    /// <summary>The radio_id this match belongs to, when the callback carried one.</summary>
    public long? RadioId { get; init; }

    /// <summary>Server-side timestamp string (e.g. <c>"2020-04-13 10:31:43"</c>).</summary>
    public string? Timestamp { get; init; }

    /// <summary>Play length in seconds, when the server reported one.</summary>
    public long? PlayLength { get; init; }

    /// <summary>The top match. Always set on a successfully parsed match.</summary>
    public StreamCallbackSong Song { get; init; } = new();

    /// <summary>
    /// Additional candidate matches when the fingerprint resolved to multiple
    /// near-identical catalog records. Possibly empty.
    ///
    /// <para><b>Heads up:</b> alternatives may carry a <i>different</i> artist or title
    /// from <see cref="Song"/> — this happens with variant catalog releases (regional
    /// re-issues, live versions, remixes, the same recording filed under a featuring
    /// artist's name). Don't assume alternatives are just metadata variations of the
    /// same song.</para>
    /// </summary>
    public IReadOnlyList<StreamCallbackSong> Alternatives { get; init; } = Array.Empty<StreamCallbackSong>();

    /// <summary>Forward-compat: any unknown top-level keys on the <c>result</c> object.</summary>
    public IReadOnlyDictionary<string, JsonElement> Extras { get; init; } = new Dictionary<string, JsonElement>();

    /// <summary>Original full callback POST body.</summary>
    [JsonIgnore]
    public JsonElement RawResponse { get; init; }
}

/// <summary>Notification variant of a streams callback POST body.</summary>
public sealed record StreamCallbackNotification
{
    [JsonPropertyName("radio_id")] public long? RadioId { get; init; }
    [JsonPropertyName("stream_running")] public bool? StreamRunning { get; init; }
    [JsonPropertyName("notification_code")] public int? NotificationCode { get; init; }
    [JsonPropertyName("notification_message")] public string NotificationMessage { get; init; } = "";

    /// <summary>The outer <c>time</c> field on the callback envelope.</summary>
    [JsonIgnore] public long? Time { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> Extras { get; set; } = new();

    /// <summary>Original full callback POST body.</summary>
    [JsonIgnore]
    public JsonElement RawResponse { get; init; }
}

/// <summary>
/// One streams-callback or longpoll event. Pattern-match on <see cref="Match"/>
/// and <see cref="Notification"/> to handle each variant.
///
/// <code>
/// var ev = AudDHelpers.ParseCallback(body);
/// switch (ev)
/// {
///     case CallbackEvent.Match m:
///         Console.WriteLine($"{m.Value.Song.Artist} - {m.Value.Song.Title}");
///         break;
///     case CallbackEvent.Notification n:
///         Console.WriteLine($"notification: {n.Value.NotificationMessage}");
///         break;
/// }
/// </code>
/// </summary>
public abstract record CallbackEvent
{
    private CallbackEvent() { }

    /// <summary>Recognition variant — wraps a <see cref="StreamCallbackMatch"/>.</summary>
    public sealed record Match(StreamCallbackMatch Value) : CallbackEvent;

    /// <summary>Notification variant — wraps a <see cref="StreamCallbackNotification"/>.</summary>
    public sealed record Notification(StreamCallbackNotification Value) : CallbackEvent;
}

/// <summary>One result from <c>advanced.find_lyrics</c>.</summary>
public sealed record LyricsResult
{
    [JsonPropertyName("artist")] public string Artist { get; init; } = "";
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("lyrics")] public string? Lyrics { get; init; }
    [JsonPropertyName("song_id")] public long? SongId { get; init; }
    [JsonPropertyName("media")] public string? Media { get; init; }
    [JsonPropertyName("full_title")] public string? FullTitle { get; init; }
    [JsonPropertyName("artist_id")] public long? ArtistId { get; init; }
    [JsonPropertyName("song_link")] public string? SongLink { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> Extras { get; set; } = new();
}

/// <summary>
/// Shared System.Text.Json options.
///
/// <para>The <see cref="JsonSerializerOptions.TypeInfoResolver"/> is wired to
/// <see cref="AudDJsonContext.Default"/> — the source-generated context that lists every
/// wire-boundary model. This means deserialization stops going through reflection,
/// so the SDK trims/AOT-publishes cleanly.</para>
///
/// <para>Direct callers should prefer <c>el.Deserialize(AudDJsonContext.Default.RecognitionResult)</c>
/// (the typed <see cref="JsonTypeInfo{T}"/> overload) over the generic
/// <c>el.Deserialize&lt;RecognitionResult&gt;()</c> — the typed overload is statically
/// resolved against the source generator and emits no IL2026/IL3050 warnings.</para>
/// </summary>
internal static class JsonOpts
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = AudDJsonContext.Default,
    };
}
