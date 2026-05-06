using System.ComponentModel.DataAnnotations;

namespace AudD.DependencyInjection;

/// <summary>
/// Strongly-typed configuration for <see cref="AudDServiceCollectionExtensions.AddAudD(Microsoft.Extensions.DependencyInjection.IServiceCollection, System.Action{AudDOptions}?)"/>.
///
/// <para>Bind from <c>IConfiguration</c> with</para>
/// <code>
/// builder.Services.AddAudD(opts =>
///     builder.Configuration.GetSection("AudD").Bind(opts));
/// </code>
///
/// <para>Or via the Options pattern:</para>
/// <code>
/// builder.Services.Configure&lt;AudDOptions&gt;(builder.Configuration.GetSection("AudD"));
/// builder.Services.AddAudD();
/// </code>
/// </summary>
public sealed class AudDOptions
{
    /// <summary>
    /// AudD API token. Required unless <c>AUDD_API_TOKEN</c> is set in the
    /// process environment — when neither is set, the client constructor
    /// throws <see cref="System.ArgumentException"/>.
    /// </summary>
    public string? ApiToken { get; set; }

    /// <summary>Maximum retry attempts per call. Default 3.</summary>
    [Range(1, 10)]
    public int MaxRetries { get; set; } = 3;

    /// <summary>Initial backoff factor in seconds (jittered, exponential). Default 0.5.</summary>
    [Range(0.0, 60.0)]
    public double BackoffFactor { get; set; } = 0.5;

    /// <summary>
    /// Optional logical name of a registered <see cref="System.Net.Http.IHttpClientFactory"/>
    /// client to source the standard-endpoint <see cref="System.Net.Http.HttpClient"/> from.
    /// When <c>null</c>, the SDK creates its own <see cref="System.Net.Http.HttpClient"/>.
    ///
    /// <para>This is the canonical .NET DI path for HTTP — your existing handlers
    /// (Polly, OpenTelemetry, mTLS) still apply.</para>
    /// </summary>
    public string? HttpClientName { get; set; }

    /// <summary>
    /// Optional logical name of a registered <see cref="System.Net.Http.IHttpClientFactory"/>
    /// client to source the enterprise-endpoint <see cref="System.Net.Http.HttpClient"/> from.
    /// When <c>null</c>, the SDK creates its own <see cref="System.Net.Http.HttpClient"/>
    /// configured with <see cref="AudD.EnterpriseTimeout"/>.
    /// </summary>
    public string? EnterpriseHttpClientName { get; set; }
}
