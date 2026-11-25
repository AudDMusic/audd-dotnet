using System.Net;
using System.Text;
using System.Web;
using AudD;
using Xunit;

namespace AudD.Tests.Unit;

/// <summary>
/// Longpoll consumer behaviour against the three documented response shapes
/// (events, timeout, error), the top-level callback shape, and bodies in none of
/// them. Both entry points share the loop: <c>audd.Streams.LongpollAsync</c>
/// (token-bound) and <c>LongpollConsumer.Iterate</c> (tokenless). Responses come
/// from a scripted in-process handler; waits between requests are recorded
/// through an injected delay instead of elapsing in real time.
/// </summary>
public class LongpollTests
{
    public enum Entry { Streams, Consumer }

    private const string Token = "longpoll-secret-token-7f3a";
    private const string Category = "92b1cc7f0";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    // ---- Events ----

    [Theory]
    [InlineData(Entry.Streams)]
    [InlineData(Entry.Consumer)]
    public async Task EventsFixture_DeliversBothSongsInOrder_ThenSendsLargestTimestamp(Entry entry)
    {
        await using var h = Harness.Create(entry, (i, _, ct) => i == 0 ? Json(Fixture("longpoll_events.json")) : Hang(ct));
        await using var poll = await h.StartAsync();

        var matches = await TakeAsync(poll.Matches, 2);
        Assert.Equal("Alan Walker, A$AP Rocky", matches[0].Song.Artist);
        Assert.Equal("Live Fast (PUBGM)", matches[0].Song.Title);
        Assert.Equal(7L, matches[0].RadioId);
        Assert.Equal("Tears For Fears", matches[1].Song.Artist);
        Assert.Equal("Everybody Wants To Rule The World", matches[1].Song.Title);
        Assert.Equal(7L, matches[1].RadioId);

        await h.Handler.WaitForRequestsAsync(2);
        var requests = h.Handler.Requests;
        Assert.Null(Query(requests[0])["since_time"]);
        Assert.Equal("1795547548112", Query(requests[1])["since_time"]);
        Assert.Equal(Category, Query(requests[1])["category"]);
        Assert.Empty(h.Recorder.Delays);
    }

    [Fact]
    public async Task Events_WithEmptyResultsOrUnparseableData_AreSkipped_AndPollingContinues()
    {
        var matchData = """{"status":"success","result":{"radio_id":9,"timestamp":"2026-11-24 19:00:00","play_length":30,"results":[{"artist":"From String","title":"Data"}]}}""";
        // Event 13 carries its data as JSON text rather than an object.
        var body = """
        {"events":[
          {"timestamp":11,"category":"c","data":{"status":"success","result":{"radio_id":9,"timestamp":"t","play_length":1,"results":[]}}},
          {"timestamp":12,"category":"c","data":"not json"},
          {"timestamp":13,"category":"c","data":MATCH_AS_STRING},
          {"timestamp":14,"category":"c","data":42},
          {"timestamp":15,"category":"c","data":{"status":"-","notification":{"radio_id":9,"stream_running":false,"notification_code":650,"notification_message":"can't connect"},"time":1587939136}},
          {"timestamp":16,"category":"c","data":{"status":"success","result":{"radio_id":9,"results":[{"artist":"Second","title":"Song"}]}}}
        ]}
        """.Replace("MATCH_AS_STRING", System.Text.Json.JsonSerializer.Serialize(matchData));
        await using var h = Harness.Create(Entry.Streams, (i, _, ct) => i == 0 ? Json(body) : Hang(ct));
        await using var poll = await h.StartAsync();

        var matches = await TakeAsync(poll.Matches, 2);
        Assert.Equal("From String", matches[0].Song.Artist);
        Assert.Equal("Second", matches[1].Song.Artist);
        var notification = Assert.Single(await TakeAsync(poll.Notifications, 1));
        Assert.Equal(650, notification.NotificationCode);

        await h.Handler.WaitForRequestsAsync(2);
        Assert.Equal("16", Query(h.Handler.Requests[1])["since_time"]);
        await poll.DisposeAsync();
        Assert.Empty(await DrainAsync(poll.Errors));
    }

    [Fact]
    public async Task EmptyEventsList_CountsAsNothingNew_AndWaitsBeforeTheNextRequest()
    {
        var recorder = new DelayRecorder(hangAfter: 1);
        await using var h = Harness.Create(Entry.Streams, (_, _, _) => Json("""{"events":[]}"""), recorder);
        await using var poll = await h.StartAsync(sinceTime: 500);

        await recorder.WaitForAsync(1);
        Assert.Equal(new[] { TimeSpan.FromSeconds(1) }, recorder.Delays);
        Assert.Equal(1, h.Handler.Count);
        Assert.Equal("500", Query(h.Handler.Requests[0])["since_time"]);
    }

    // ---- Timeout ----

    [Theory]
    [InlineData(Entry.Streams)]
    [InlineData(Entry.Consumer)]
    public async Task TimeoutFixture_DeliversNothing_NextRequestCarriesItsTimestamp(Entry entry)
    {
        await using var h = Harness.Create(entry, (i, _, ct) => i == 0 ? Json(Fixture("longpoll_no_events.json")) : Hang(ct));
        await using var poll = await h.StartAsync();

        await h.Handler.WaitForRequestsAsync(2);
        Assert.Equal("1777901270049", Query(h.Handler.Requests[1])["since_time"]);
        Assert.Empty(h.Recorder.Delays);

        await poll.DisposeAsync();
        Assert.Empty(await DrainAsync(poll.Matches));
        Assert.Empty(await DrainAsync(poll.Notifications));
        Assert.Empty(await DrainAsync(poll.Errors));
    }

    // ---- Error body ----

    [Theory]
    [InlineData(Entry.Streams)]
    [InlineData(Entry.Consumer)]
    public async Task ErrorFixture_ReachesErrorsOnce_AfterExactlyOneRequest(Entry entry)
    {
        await using var h = Harness.Create(entry, (_, _, _) => Json(Fixture("longpoll_error.json")));
        await using var poll = await h.StartAsync(timeout: 500);

        var errors = await DrainAsync(poll.Errors);
        var error = Assert.IsType<AudDInvalidRequestException>(Assert.Single(errors));
        Assert.Equal("Invalid timeout arg.  Must be 1-120.", error.ServerMessage);
        Assert.Empty(await DrainAsync(poll.Matches));
        Assert.Equal(1, h.Handler.Count);
        Assert.Empty(h.Recorder.Delays);
    }

    [Fact]
    public async Task StatusErrorBody_ReachesErrorsAsTypedException_AndStops()
    {
        await using var h = Harness.Create(Entry.Consumer,
            (_, _, _) => Json("""{"status":"error","error":{"error_code":900,"error_message":"bad token"}}"""));
        await using var poll = await h.StartAsync();

        var error = Assert.IsType<AudDAuthenticationException>(Assert.Single(await DrainAsync(poll.Errors)));
        Assert.Equal(900, error.ErrorCode);
        Assert.Equal(1, h.Handler.Count);
    }

    // ---- Top-level callback shape ----

    [Theory]
    [InlineData(Entry.Streams)]
    [InlineData(Entry.Consumer)]
    public async Task TopLevelResultShape_StillDelivers_AndUsesItsTimestamp(Entry entry)
    {
        const string body = """{"status":"success","result":{"radio_id":3,"timestamp":"2026-01-01 00:00:00","play_length":100,"results":[{"artist":"A","title":"T"}]},"timestamp":1700000000000}""";
        await using var h = Harness.Create(entry, (i, _, ct) => i == 0 ? Json(body) : Hang(ct));
        await using var poll = await h.StartAsync();

        var match = Assert.Single(await TakeAsync(poll.Matches, 1));
        Assert.Equal("A", match.Song.Artist);
        Assert.Equal(3L, match.RadioId);

        await h.Handler.WaitForRequestsAsync(2);
        Assert.Equal("1700000000000", Query(h.Handler.Requests[1])["since_time"]);
    }

    [Fact]
    public async Task TopLevelNotificationShape_StillDelivers()
    {
        await using var h = Harness.Create(Entry.Streams, (i, _, ct) => i == 0
            ? Json("""{"status":"-","notification":{"radio_id":3,"stream_running":false,"notification_code":650,"notification_message":"can't connect"},"time":1587939136}""")
            : Hang(ct));
        await using var poll = await h.StartAsync();

        var n = Assert.Single(await TakeAsync(poll.Notifications, 1));
        Assert.Equal(3L, n.RadioId);
        Assert.Equal(650, n.NotificationCode);
    }

    // ---- Bodies in none of the shapes ----

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("""{"something":"else"}""")]
    [InlineData("""{"timeout":"no events before timeout"}""")]
    [InlineData("""{"events":"not a list"}""")]
    public async Task UnrecognizedBody_DoesNotSpin_BacksOffFromOneSecondToThirty(string body)
    {
        var recorder = new DelayRecorder(hangAfter: 8);
        await using var h = Harness.Create(Entry.Streams, (_, _, _) => Json(body), recorder);
        await using var poll = await h.StartAsync();

        await recorder.WaitForAsync(8);
        Assert.Equal(new[] { 1, 2, 4, 8, 16, 30, 30, 30 }, recorder.Delays.Select(d => (int)d.TotalSeconds));
        // One request per wait: the eighth wait is still pending, so no ninth request.
        Assert.Equal(8, h.Handler.Count);
        Assert.Empty(await TakeAsync(poll.Errors, 1, TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public async Task UnrecognizedBody_WithTheRealDelay_SendsOneRequestInTheFirstSecond_AndDisposeInterruptsTheWait()
    {
        await using var h = Harness.Create(Entry.Consumer, (_, _, _) => Json("<html>maintenance</html>"), realDelay: true);
        var poll = await h.StartAsync();

        await h.Handler.WaitForRequestsAsync(1);
        await Task.Delay(300);
        Assert.Equal(1, h.Handler.Count);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await poll.DisposeAsync();
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(700), $"DisposeAsync took {sw.Elapsed}");
        Assert.Equal(1, h.Handler.Count);
    }

    [Fact]
    public async Task EventsWithoutAnyTimestamp_WaitBeforeTheNextRequest()
    {
        const string body = """{"events":[{"data":{"status":"success","result":{"radio_id":1,"results":[{"artist":"A","title":"T"}]}}}]}""";
        var recorder = new DelayRecorder(hangAfter: 1);
        await using var h = Harness.Create(Entry.Streams, (_, _, _) => Json(body), recorder);
        await using var poll = await h.StartAsync();

        Assert.Single(await TakeAsync(poll.Matches, 1));
        await recorder.WaitForAsync(1);
        Assert.Equal(1, h.Handler.Count);
    }

    // ---- Transport and HTTP failures ----

    [Fact]
    public async Task Transient5xx_IsRetried_AndPollingContinues()
    {
        await using var h = Harness.Create(Entry.Streams, (i, _, ct) => i switch
        {
            0 => Html(HttpStatusCode.ServiceUnavailable),
            1 => Json(Fixture("longpoll_events.json")),
            _ => Hang(ct),
        });
        await using var poll = await h.StartAsync();

        Assert.Equal(2, (await TakeAsync(poll.Matches, 2)).Count);
        await h.Handler.WaitForRequestsAsync(3);
        Assert.Empty(await TakeAsync(poll.Errors, 1, TimeSpan.FromMilliseconds(50)));
    }

    [Theory]
    [InlineData(Entry.Streams)]
    [InlineData(Entry.Consumer)]
    public async Task Persistent5xx_ReachesErrorsAfterTheRetryBudget(Entry entry)
    {
        await using var h = Harness.Create(entry, (_, _, _) => Html(HttpStatusCode.BadGateway), maxRetries: 3);
        await using var poll = await h.StartAsync();

        var error = Assert.IsType<AudDServerException>(Assert.Single(await DrainAsync(poll.Errors)));
        Assert.Equal(502, error.HttpStatus);
        Assert.Equal(3, h.Handler.Count);
    }

    [Theory]
    [InlineData(Entry.Streams)]
    [InlineData(Entry.Consumer)]
    public async Task ConnectionFailure_ReachesErrorsAsAudDConnectionException_WithoutTheToken(Entry entry)
    {
        // A handler that echoes the full request URL into its message, as some
        // proxies and handlers do: the URL must not carry the token.
        await using var h = Harness.Create(entry,
            (_, req, _) => throw new HttpRequestException($"connect failed for {req.RequestUri}"), maxRetries: 2);
        await using var poll = await h.StartAsync();

        var error = Assert.IsType<AudDConnectionException>(Assert.Single(await DrainAsync(poll.Errors)));
        Assert.Contains("/longpoll/", error.Message);
        Assert.DoesNotContain(Token, error.ToString());
        Assert.Equal(2, h.Handler.Count);
    }

    [Fact]
    public async Task RequestDeadline_ReachesErrorsAsAudDConnectionException()
    {
        // HttpClient.Timeout elapsing surfaces as TaskCanceledException without the caller's token cancelled.
        await using var h = Harness.Create(Entry.Streams,
            (_, _, _) => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"),
            maxRetries: 1);
        await using var poll = await h.StartAsync();

        Assert.IsType<AudDConnectionException>(Assert.Single(await DrainAsync(poll.Errors)));
    }

    // ---- Token ----

    [Fact]
    public async Task LongpollRequest_CarriesNoApiToken()
    {
        await using var h = Harness.Create(Entry.Streams, (i, _, ct) => i == 0 ? Json(Fixture("longpoll_no_events.json")) : Hang(ct));
        await using var poll = await h.StartAsync();

        await h.Handler.WaitForRequestsAsync(2);
        foreach (var r in h.Handler.Requests)
        {
            Assert.Equal(HttpMethod.Get, r.Method);
            Assert.Equal("/longpoll/", r.Uri.AbsolutePath);
            Assert.Null(Query(r)["api_token"]);
            Assert.DoesNotContain(Token, r.Uri.ToString());
            Assert.DoesNotContain(Token, r.Headers);
            Assert.False(r.HasContent);
        }
    }

    // ---- Cancellation and disposal ----

    [Theory]
    [InlineData(Entry.Streams)]
    [InlineData(Entry.Consumer)]
    public async Task DisposingThePoll_InterruptsAWaitingRequest(Entry entry)
    {
        await using var h = Harness.Create(entry, (_, _, ct) => Hang(ct));
        var poll = await h.StartAsync();
        await h.Handler.WaitForRequestsAsync(1);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await poll.DisposeAsync();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"DisposeAsync took {sw.Elapsed}");
        Assert.Equal(1, h.Handler.CancelledCount);
        Assert.Empty(await DrainAsync(poll.Errors));
    }

    [Fact]
    public async Task DisposingTheConsumer_StopsItsPolls_DuringARequestAndDuringAWait()
    {
        using var handler = new ScriptedHandler((i, _, ct) => i == 0 ? Json("{}") : Hang(ct));
        using var client = new HttpClient(handler);
        var consumer = new LongpollConsumer(Category, httpClient: client);
        var recorder = new DelayRecorder(hangAfter: 1);
        consumer.IdleDelay = recorder.DelayAsync;

        var waiting = consumer.Iterate();          // first response is unrecognized: waits
        await recorder.WaitForAsync(1);
        var requesting = consumer.Iterate();       // second request hangs
        await handler.WaitForRequestsAsync(2);

        await consumer.DisposeAsync();
        Assert.Empty(await DrainAsync(waiting.Matches));
        Assert.Empty(await DrainAsync(requesting.Matches));
        Assert.Empty(await DrainAsync(waiting.Errors));
        Assert.Empty(await DrainAsync(requesting.Errors));
        Assert.Equal(1, handler.CancelledCount);
        Assert.Throws<ObjectDisposedException>(() => consumer.Iterate());
        await waiting.DisposeAsync();
        await requesting.DisposeAsync();
    }

    [Fact]
    public async Task CancellingTheToken_InterruptsTheWait()
    {
        var recorder = new DelayRecorder(hangAfter: 1);
        await using var h = Harness.Create(Entry.Streams, (_, _, _) => Json("{}"), recorder);
        using var cts = new CancellationTokenSource();
        await using var poll = await h.StartAsync(ct: cts.Token);
        await recorder.WaitForAsync(1);

        cts.Cancel();
        Assert.Empty(await DrainAsync(poll.Matches));
        Assert.Empty(await DrainAsync(poll.Errors));
        Assert.Equal(1, h.Handler.Count);
    }

    // ---- Harness ----

    private static string Fixture(string name)
        => File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static Task<HttpResponseMessage> Json(string body)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });

    private static Task<HttpResponseMessage> Html(HttpStatusCode status)
        => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent("<html>upstream error</html>", Encoding.UTF8, "text/html"),
        });

    private static async Task<HttpResponseMessage> Hang(CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        throw new InvalidOperationException("unreachable");
    }

    private static System.Collections.Specialized.NameValueCollection Query(RecordedRequest r)
        => HttpUtility.ParseQueryString(r.Uri.Query);

    private static async Task<List<T>> TakeAsync<T>(IAsyncEnumerable<T> source, int count, TimeSpan? timeout = null)
    {
        var list = new List<T>();
        using var cts = new CancellationTokenSource(timeout ?? Wait);
        try
        {
            await foreach (var item in source.WithCancellation(cts.Token))
            {
                list.Add(item);
                if (list.Count == count) break;
            }
        }
        catch (OperationCanceledException) when (timeout is not null)
        {
            // A short window that elapsed: return what arrived.
        }
        return list;
    }

    private static async Task<List<T>> DrainAsync<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        using var cts = new CancellationTokenSource(Wait);
        await foreach (var item in source.WithCancellation(cts.Token)) list.Add(item);
        return list;
    }

    private sealed record RecordedRequest(HttpMethod Method, Uri Uri, string Headers, bool HasContent);

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;
        private readonly List<RecordedRequest> _requests = new();
        private int _cancelled;

        public ScriptedHandler(Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        {
            _respond = respond;
        }

        public IReadOnlyList<RecordedRequest> Requests { get { lock (_requests) return _requests.ToList(); } }
        public int Count { get { lock (_requests) return _requests.Count; } }
        public int CancelledCount => Volatile.Read(ref _cancelled);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int index;
            lock (_requests)
            {
                index = _requests.Count;
                _requests.Add(new RecordedRequest(request.Method, request.RequestUri!, request.Headers.ToString(), request.Content is not null));
            }
            try
            {
                return await _respond(index, request, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref _cancelled);
                throw;
            }
        }

        public async Task WaitForRequestsAsync(int count)
        {
            var deadline = DateTime.UtcNow + Wait;
            while (Count < count)
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException($"saw {Count} of {count} requests");
                await Task.Delay(10);
            }
        }
    }

    /// <summary>Records each requested wait; completes the first ones at once and holds the rest until cancelled.</summary>
    private sealed class DelayRecorder
    {
        private readonly int _hangAfter;
        private readonly List<TimeSpan> _delays = new();

        public DelayRecorder(int hangAfter) { _hangAfter = hangAfter; }

        public IReadOnlyList<TimeSpan> Delays { get { lock (_delays) return _delays.ToList(); } }

        public Task DelayAsync(TimeSpan delay, CancellationToken ct)
        {
            int n;
            lock (_delays)
            {
                _delays.Add(delay);
                n = _delays.Count;
            }
            return n >= _hangAfter ? Task.Delay(Timeout.Infinite, ct) : Task.CompletedTask;
        }

        public async Task WaitForAsync(int count)
        {
            var deadline = DateTime.UtcNow + Wait;
            while (Delays.Count < count)
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException($"saw {Delays.Count} of {count} waits");
                await Task.Delay(10);
            }
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly HttpClient _client;
        private readonly global::AudD.AudD? _audd;
        private readonly LongpollConsumer? _consumer;

        private Harness(ScriptedHandler handler, HttpClient client, global::AudD.AudD? audd, LongpollConsumer? consumer, DelayRecorder recorder)
        {
            Handler = handler;
            _client = client;
            _audd = audd;
            _consumer = consumer;
            Recorder = recorder;
        }

        public ScriptedHandler Handler { get; }
        public DelayRecorder Recorder { get; }

        /// <summary>
        /// <paramref name="recorder"/> defaults to one that records waits and holds the
        /// first one; <paramref name="realDelay"/> keeps the SDK's own <c>Task.Delay</c>.
        /// </summary>
        public static Harness Create(
            Entry entry,
            Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond,
            DelayRecorder? recorder = null,
            int maxRetries = 3,
            bool realDelay = false)
        {
            recorder ??= new DelayRecorder(hangAfter: 1);
            var handler = new ScriptedHandler(respond);
            var client = new HttpClient(handler);
            Func<TimeSpan, CancellationToken, Task>? delay = realDelay ? null : recorder.DelayAsync;
            if (entry == Entry.Streams)
            {
                var audd = new global::AudD.AudD(Token, maxRetries: maxRetries, backoffFactor: 0,
                    httpClient: client, enterpriseHttpClient: client);
                audd.Streams.IdleDelay = delay;
                return new Harness(handler, client, audd, null, recorder);
            }
            var consumer = new LongpollConsumer(Category, httpClient: client, maxRetries: maxRetries, backoffFactor: 0);
            consumer.IdleDelay = delay;
            return new Harness(handler, client, null, consumer, recorder);
        }

        public async Task<LongpollPoll> StartAsync(long? sinceTime = null, int timeout = 50, CancellationToken ct = default)
        {
            if (_audd is not null)
            {
                return await _audd.Streams.LongpollAsync(Category, sinceTime, timeout, skipCallbackCheck: true, cancellationToken: ct);
            }
            return _consumer!.Iterate(sinceTime, timeout, ct);
        }

        public async ValueTask DisposeAsync()
        {
            if (_consumer is not null) await _consumer.DisposeAsync();
            if (_audd is not null) await _audd.DisposeAsync();
            _client.Dispose();
            Handler.Dispose();
        }
    }
}
