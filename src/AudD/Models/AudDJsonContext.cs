using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudD;

/// <summary>
/// System.Text.Json source-generated metadata for every wire-boundary type used by the SDK.
///
/// <para>This is the resolver wired into <see cref="JsonOpts.Default"/>. Source generation
/// avoids reflection at runtime, which makes the SDK trim- and AOT-clean: dotnet publish
/// with <c>-p:PublishTrimmed=true</c> or <c>-p:PublishAot=true</c> emits no
/// IL2026/IL3050 warnings against this assembly.</para>
///
/// <para>If you add a new model that flows through the wire boundary (response decoding
/// or callback parsing), register it here with another <c>[JsonSerializable]</c> attribute.
/// Otherwise reflection-based fallback will trim away and the type will fail to deserialize
/// in AOT builds.</para>
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(RecognitionResult))]
[JsonSerializable(typeof(EnterpriseMatch))]
[JsonSerializable(typeof(EnterpriseChunkResult))]
[JsonSerializable(typeof(Stream))]
[JsonSerializable(typeof(StreamCallbackMatch))]
[JsonSerializable(typeof(StreamCallbackSong))]
[JsonSerializable(typeof(StreamCallbackNotification))]
[JsonSerializable(typeof(LyricsResult))]
[JsonSerializable(typeof(AppleMusicMetadata))]
[JsonSerializable(typeof(SpotifyMetadata))]
[JsonSerializable(typeof(DeezerMetadata))]
[JsonSerializable(typeof(NapsterMetadata))]
[JsonSerializable(typeof(MusicBrainzEntry))]
[JsonSerializable(typeof(System.Collections.Generic.IReadOnlyList<EnterpriseMatch>))]
[JsonSerializable(typeof(System.Collections.Generic.IReadOnlyList<MusicBrainzEntry>))]
[JsonSerializable(typeof(System.Collections.Generic.IReadOnlyList<StreamCallbackSong>))]
[JsonSerializable(typeof(JsonElement))]
public sealed partial class AudDJsonContext : JsonSerializerContext
{
}
