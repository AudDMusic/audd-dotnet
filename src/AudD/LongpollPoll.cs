using System.Threading.Channels;
using AudD.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
/// <para>A background loop sends one request at a time and delivers each event once,
/// in order: every request carries the newest event timestamp (or the last
/// "no events" response's timestamp) as <c>since_time</c>. The loop stops on its own
/// only for a terminal error: the server rejecting the request (for example a
/// <c>timeout</c> outside 1–120 seconds), or a network or HTTP failure that outlasts
/// the retries. That error is written to <see cref="Errors"/> before the other two
/// streams close, so once <see cref="Matches"/> ends, <see cref="Errors"/> says why.
/// A response in none of the documented shapes delivers nothing, and the next request
/// waits 1 second, doubling up to 30 seconds while such responses continue.</para>
///
/// <para>Iterate any one of the three with <c>await foreach</c>; multiple consumers can
/// iterate in parallel from separate tasks. Disposing the poll, or cancelling the
/// token it was started with, stops the loop, including a request or a wait in
/// progress, and closes all three streams.</para>
///
/// <example>
/// <code>
/// await using var poll = await audd.Streams.LongpollAsync(category);
/// await foreach (var m in poll.Matches.WithCancellation(ct))
/// {
///     Console.WriteLine($"{m.Song.Artist} — {m.Song.Title}");
/// }
/// await foreach (var error in poll.Errors)
/// {
///     Console.Error.WriteLine($"longpoll stopped: {error.Message}");
/// }
/// </code>
/// </example>
/// </summary>
public sealed class LongpollPoll : IAsyncDisposable, IDisposable
{
    /// <summary>First wait after a response that moved nothing forward.</summary>
    internal static readonly TimeSpan IdleDelayInitial = TimeSpan.FromSeconds(1);

    /// <summary>Longest wait between requests while responses keep moving nothing forward.</summary>
    internal static readonly TimeSpan IdleDelayMax = TimeSpan.FromSeconds(30);

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

    /// <summary>Stream-lifecycle notifications. Closes when the poll terminates.</summary>
    public IAsyncEnumerable<StreamCallbackNotification> Notifications => _notifications.Reader.ReadAllAsync();

    /// <summary>
    /// At most one terminal error: <see cref="AudDInvalidRequestException"/> when the
    /// server rejects the request, <see cref="AudDConnectionException"/> or
    /// <see cref="AudDServerException"/> when the network or the server keeps failing
    /// after the retries. It is written before <see cref="Matches"/> and
    /// <see cref="Notifications"/> close. Closes empty when the poll is disposed or
    /// cancelled.
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
        => Start(fetcher, CancellationTokenSource.CreateLinkedTokenSource(externalCancellation));

    /// <summary>Start a longpoll that stops when either token is cancelled.</summary>
    internal static LongpollPoll Start(LongpollFetcher fetcher, CancellationToken externalCancellation, CancellationToken ownerCancellation)
        => Start(fetcher, CancellationTokenSource.CreateLinkedTokenSource(externalCancellation, ownerCancellation));

    private static LongpollPoll Start(LongpollFetcher fetcher, CancellationTokenSource cts)
    {
        var matches = Channel.CreateUnbounded<StreamCallbackMatch>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });
        var notifications = Channel.CreateUnbounded<StreamCallbackNotification>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });
        var errors = Channel.CreateUnbounded<Exception>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });
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
            var idleDelay = IdleDelayInitial;
            while (!ct.IsCancellationRequested)
            {
                LongpollBatch batch;
                try
                {
                    batch = await fetcher.FetchAsync(sinceTime, ct).ConfigureAwait(false);
                }
                catch (Exception) when (ct.IsCancellationRequested)
                {
                    // Disposal or cancellation interrupted the request.
                    return;
                }
                catch (Exception ex)
                {
                    errors.Writer.TryWrite(ex);
                    return;
                }

                foreach (var ev in batch.Events)
                {
                    switch (ev)
                    {
                        case CallbackEvent.Match m:
                            await matches.Writer.WriteAsync(m.Value, ct).ConfigureAwait(false);
                            break;
                        case CallbackEvent.Notification n:
                            await notifications.Writer.WriteAsync(n.Value, ct).ConfigureAwait(false);
                            break;
                    }
                }

                var advanced = batch.Timestamp is long next && next != sinceTime;
                if (batch.Timestamp is long ts) sinceTime = ts;
                if (advanced)
                {
                    idleDelay = IdleDelayInitial;
                    continue;
                }

                // Nothing moved since_time forward, so the same request would get the
                // same answer at once: wait before sending it.
                if (batch.Events.Count > 0) idleDelay = IdleDelayInitial;
                if (!batch.Recognized)
                {
                    fetcher.Logger.LogWarning(
                        "AudD longpoll: response had no events and no timestamp; next request in {Delay}",
                        idleDelay);
                }
                await fetcher.Delay(idleDelay, ct).ConfigureAwait(false);
                idleDelay = TimeSpan.FromTicks(Math.Min(idleDelay.Ticks * 2, IdleDelayMax.Ticks));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* normal teardown */ }
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
}

/// <summary>
/// Per-poll fetch closure. Implementations vary (token-bound, tokenless). The fetch
/// throws for a terminal error and returns a batch (possibly empty) otherwise.
/// </summary>
internal sealed class LongpollFetcher
{
    public Func<long?, CancellationToken, Task<LongpollBatch>> FetchAsync { get; }
    public long? InitialSinceTime { get; }

    /// <summary>The wait between requests that moved nothing forward. Tests substitute it.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; }

    public ILogger Logger { get; }

    public LongpollFetcher(
        Func<long?, CancellationToken, Task<LongpollBatch>> fetchAsync,
        long? initialSinceTime,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        ILogger? logger = null)
    {
        FetchAsync = fetchAsync;
        InitialSinceTime = initialSinceTime;
        Delay = delay ?? Task.Delay;
        Logger = logger ?? NullLogger.Instance;
    }
}
