using System;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AudD.DependencyInjection;

/// <summary>
/// <see cref="IServiceCollection"/> extension for ASP.NET Core / Worker Service /
/// Generic Host integration. Registers <see cref="AudD"/> as a singleton.
///
/// <example>
/// <code>
/// // Program.cs
/// var builder = WebApplication.CreateBuilder(args);
///
/// builder.Services.AddAudD(opts =>
/// {
///     builder.Configuration.GetSection("AudD").Bind(opts);
/// });
///
/// var app = builder.Build();
/// // Inject AudD into your controller/handler/service.
/// </code>
/// </example>
/// </summary>
public static class AudDServiceCollectionExtensions
{
    /// <summary>
    /// Register the AudD client as a singleton <see cref="AudD"/> service.
    ///
    /// <para>Wires <see cref="ILoggerFactory"/>, <see cref="IOptions{TOptions}"/> for
    /// <see cref="AudDOptions"/>, and <see cref="IHttpClientFactory"/> when present
    /// (auto-discovered from the container — call <c>AddHttpClient()</c> beforehand
    /// to opt in).</para>
    /// </summary>
    /// <param name="services">The DI container.</param>
    /// <param name="configure">
    /// Optional callback to populate <see cref="AudDOptions"/>. When <c>null</c>,
    /// the caller is expected to have configured <see cref="AudDOptions"/> via
    /// <see cref="OptionsServiceCollectionExtensions.Configure{TOptions}(IServiceCollection, Action{TOptions})"/>
    /// or <c>builder.Configuration.GetSection("AudD")</c> ahead of this call.
    /// </param>
    /// <returns>An <see cref="AudDBuilder"/> for chained configuration (e.g. <c>.WithOnEvent(...)</c>).</returns>
    public static AudDBuilder AddAudD(
        this IServiceCollection services,
        Action<AudDOptions>? configure = null)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));

        services.AddOptions();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        // Register IHttpClientFactory (no-op if already registered). This is the
        // canonical DI path; consumers who customize via AddHttpClient still see
        // their named/typed-client registrations honored.
        services.AddHttpClient();

        services.AddSingleton<AudD>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<AudDOptions>>().Value;
            var loggerFactory = sp.GetService<ILoggerFactory>();
            var logger = loggerFactory?.CreateLogger<AudD>();

            // Pull the optional onEvent hook the user wired via WithOnEvent(...).
            var hook = sp.GetService<AudDEventHookHolder>()?.OnEvent;

            var clientFactory = sp.GetService<IHttpClientFactory>();
            if (clientFactory is not null)
            {
                // Resolve a fresh HttpClient from the factory per request rather
                // than capturing one at construction — that keeps IHttpClientFactory's
                // handler rotation intact and never inherits the factory client's
                // 100s default timeout (the SDK enforces its own deadline).
                var httpName = opts.HttpClientName;
                var enterpriseName = opts.EnterpriseHttpClientName;
                Func<HttpClient> httpResolver = httpName is null
                    ? () => clientFactory.CreateClient()
                    : () => clientFactory.CreateClient(httpName);
                Func<HttpClient> enterpriseResolver = enterpriseName is null
                    ? () => clientFactory.CreateClient()
                    : () => clientFactory.CreateClient(enterpriseName);

                return new AudD(
                    apiToken: opts.ApiToken,
                    httpClientResolver: httpResolver,
                    enterpriseHttpClientResolver: enterpriseResolver,
                    maxRetries: opts.MaxRetries,
                    backoffFactor: opts.BackoffFactor,
                    logger: logger,
                    onEvent: hook);
            }

            return new AudD(
                apiToken: opts.ApiToken,
                maxRetries: opts.MaxRetries,
                backoffFactor: opts.BackoffFactor,
                logger: logger,
                onEvent: hook);
        });

        return new AudDBuilder(services);
    }
}

/// <summary>
/// Fluent builder returned by <see cref="AudDServiceCollectionExtensions.AddAudD(IServiceCollection, Action{AudDOptions}?)"/>.
/// Lets the caller chain additional registration steps (e.g. inspection hooks).
/// </summary>
public sealed class AudDBuilder
{
    /// <summary>The service collection being configured.</summary>
    public IServiceCollection Services { get; }

    internal AudDBuilder(IServiceCollection services)
    {
        Services = services;
    }

    /// <summary>
    /// Wire an <see cref="AudDEvent"/> inspection hook with access to the DI
    /// container — useful for emitting metrics or logs through
    /// <see cref="ILogger{TCategoryName}"/> resolved from the request scope.
    ///
    /// <para>Hook exceptions are caught and logged at debug-level by the SDK,
    /// so observability never breaks a request.</para>
    /// </summary>
    public AudDBuilder WithOnEvent(Action<IServiceProvider, AudDEvent> onEvent)
    {
        if (onEvent is null) throw new ArgumentNullException(nameof(onEvent));
        Services.AddSingleton<AudDEventHookHolder>(sp => new AudDEventHookHolder(evt => onEvent(sp, evt)));
        return this;
    }

    /// <summary>
    /// Wire a simple <see cref="AudDEvent"/> inspection hook that does not need
    /// access to the DI container.
    /// </summary>
    public AudDBuilder WithOnEvent(Action<AudDEvent> onEvent)
    {
        if (onEvent is null) throw new ArgumentNullException(nameof(onEvent));
        Services.AddSingleton<AudDEventHookHolder>(new AudDEventHookHolder(onEvent));
        return this;
    }
}

/// <summary>Internal carrier for the optional <c>onEvent</c> hook configured via <see cref="AudDBuilder.WithOnEvent(Action{AudDEvent})"/>.</summary>
internal sealed class AudDEventHookHolder
{
    public Action<AudDEvent> OnEvent { get; }
    public AudDEventHookHolder(Action<AudDEvent> onEvent) { OnEvent = onEvent; }
}
