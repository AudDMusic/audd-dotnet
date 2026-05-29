using System.Runtime.InteropServices;

namespace AudD.Internal;

internal static class UserAgent
{
    /// <summary>
    /// "audd-dotnet/&lt;ver&gt; dotnet/&lt;runtime-ver&gt; (&lt;os&gt;)" — see spec §7.6.
    /// </summary>
    public static string Build()
    {
        var rt = RuntimeInformation.FrameworkDescription; // e.g. ".NET 8.0.0"
        var os = RuntimeInformation.OSDescription;
        return $"audd-dotnet/{AudDVersion.Version} {rt} ({os})";
    }
}
