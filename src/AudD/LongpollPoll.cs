using System.Text.Json;
using System.Threading.Channels;
using AudD.Internal;

namespace AudD;

/// <summary>
/// An active long-poll subscription. Three typed <see cref="IAsyncEnumerable{T}"/>
/// streams surface the poll's output:
///
/// <list type="bullet">
///   <item><description><see cref="Matches"/> — recognition events.</description></item>
///   <item><description><see cref="Notifications"/> — stream-lifecycle events.</description></item>
///   <item><description><see cref="Errors"/> — at most one terminal error; after it
///     fires, <see cref="Matches"/> and <see cref="Notifications"/> close too.</description></item>
/// </list>
///
/// <para>Iterate any one of the three with <c>await foreach</c>; multiple consumers can
/// iterate in parallel from separate tasks. Disposing the poll cancels the background
/// fetch loop and closes all three streams.</para>
///
/// <example>
/// <code>
/// await using var poll = await audd.Streams.LongpollAsync(category);
/// await foreach (var m in poll.Matches.WithCancellation(ct))
/// {
///     Console.WriteLine($"{m.Song.Artist} — {m.Song.Title}");
/// }
/// </code>
/// </example>
/// </summary>
public sealed class LongpollPoll : IAsyncDisposable, IDisposable
{
    private readonly Channel<StreamCallbackMatch> _matches;
    private readonly Channel<StreamCallbackNotification> _notifications;
    private readonly Channel<Exception> _errors;
    private readonly CancellationTokenSource _cts;
    private readonly Task _runner;
    private int _disposed;

    internal LongpollPoll(
        Channel<StreamCallbackMatch> matches,
        Channel<StreamCallbackNotification> notifications,
        Channel<Exception> errors,
        CancellationTokenSource cts,
        Task runner)
    {
        _matches = matches;
        _notifications = notifications;
        _errors = errors;
        _cts = cts;
        _runner = runner;
    }

    /// <summary>Recognition events. Closes when the poll terminates.</summary>
    public IAsyncEnumerable<StreamCallbackMatch> Matches => _matches.Reader.ReadAllAsync();

    /// <summary>Stream-lifecycle notifications.</summary>
    public IAsyncEnumerable<StreamCallbackNotification> Notifications => _notifications.Reader.ReadAllAsync();

    /// <summary>
    /// At most one terminal error. After firing, <see cref="Matches"/> and
    /// <see cref="Notifications"/> also close.
    /// </summary>
    public IAsyncEnumerable<Exception> Errors => _errors.Reader.ReadAllAsync();

    /// <summary>Cancel the background fetch loop and wait for it to exit.</summary>
    public async ValueTask DisposeAsync()
    {
        if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _cts.Cancel(); } catch { /* ignore */ }
        try { await _runner.ConfigureAwait(false); } catch { /* runner reports errors via Errors */ }
        _cts.Dispose();
    }

    /// <inheritdoc/>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    // ---- Internal driver ------------------------------------------------

    /// <summary>
    /// Start a longpoll. Spawns the background loop and returns the handle.
    /// </summary>
    internal static LongpollPoll Start(LongpollFetcher fetcher, CancellationToken externalCancellation)
    {
        var matches = Channel.CreateUnbounded<StreamCallbackMatch>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });
        var notifications = Channel.CreateUnbounded<StreamCallbackNotification>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });
        var errors = Channel.CreateUnbounded<Exception>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });
        var cts = CancellationTokenSource.CreateLinkedTokenSource(externalCancellation);
        var runner = Task.Run(() => RunAsync(fetcher, matches, notifications, errors, cts.Token));
        return new LongpollPoll(matches, notifications, errors, cts, runner);
    }

    private static async Task RunAsync(
        LongpollFetcher fetcher,
        Channel<StreamCallbackMatch> matches,
        Channel<StreamCallbackNotification> notifications,
        Channel<Exception> errors,
        CancellationToken ct)
    {
        try
        {
            long? sinceTime = fetcher.InitialSinceTime;
            while (!ct.IsCancellationRequested)
            {
                JsonElement body;
                try
                {
                    body = await fetcher.FetchAsync(sinceTime, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    errors.Writer.TryWrite(ex);
                    return;
                }

                // Keepalive: server sends {"timeout":"no events before timeout"} when
                // nothing happened during the longpoll window. Advance since_time and
                // keep polling. NEVER yield to consumers.
                if (IsKeepalive(body))
                {
                    if (body.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.Number)
                    {
                        sinceTime = ts.GetInt64();
                    }
                    continue;
                }

                CallbackEvent ev;
                try
                {
                    ev = AudDHelpers.ParseCallback(body);
                }
                catch (Exception ex)
                {
                    errors.Writer.TryWrite(ex);
                    return;
                }

                switch (ev)
                {
                    case CallbackEvent.Match m:
                        await matches.Writer.WriteAsync(m.Value, ct).ConfigureAwait(false);
                        break;
                    case CallbackEvent.Notification n:
                        await notifications.Writer.WriteAsync(n.Value, ct).ConfigureAwait(false);
                        break;
                }

                if (body.TryGetProperty("timestamp", out var nextTs) && nextTs.ValueKind == JsonValueKind.Number)
                {
                    sinceTime = nextTs.GetInt64();
                }
            }
        }
        catch (OperationCanceledException) { /* normal teardown */ }
        catch (Exception ex)
        {
            errors.Writer.TryWrite(ex);
        }
        finally
        {
            matches.Writer.TryComplete();
            notifications.Writer.TryComplete();
            errors.Writer.TryComplete();
        }
    }

    /// <summary>
    /// True for <c>{"timeout":"no events before timeout"}</c> no-events ticks: the
    /// server sends one of these every &lt;timeout&gt; seconds when no recognition or
    /// notification is queued. Consumers should never see these.
    /// </summary>
    private static bool IsKeepalive(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) return false;
        if (body.TryGetProperty("result", out _)) return false;
        if (body.TryGetProperty("notification", out _)) return false;
        return body.TryGetProperty("timeout", out _);
    }
}

/// <summary>Per-poll fetch closure. Implementations vary (token-bound, tokenless).</summary>
internal sealed class LongpollFetcher
{
    public Func<long?, CancellationToken, Task<JsonElement>> FetchAsync { get; }
    public long? InitialSinceTime { get; }

    public LongpollFetcher(Func<long?, CancellationToken, Task<JsonElement>> fetchAsync, long? initialSinceTime)
    {
        FetchAsync = fetchAsync;
        InitialSinceTime = initialSinceTime;
    }
}
