namespace AudD.Internal;

/// <summary>
/// Async retry helper. Each attempt invokes the <c>attempt</c> callback which
/// must build its own <see cref="HttpContent"/> (the source re-opener gives us
/// fresh form bodies each call). HttpClient does not auto-rewind streams.
/// </summary>
internal static class Retry
{
    private static readonly Random _rng = new();

    public static async Task<HttpResponseEnvelope> RunAsync(
        Func<CancellationToken, Task<HttpResponseEnvelope>> attempt,
        RetryPolicy policy,
        CancellationToken cancellationToken)
    {
        Exception? lastExc = null;
        HttpResponseEnvelope? lastResp = null;
        for (var i = 0; i < policy.MaxAttempts; i++)
        {
            try
            {
                var resp = await attempt(cancellationToken).ConfigureAwait(false);
                if (!RetryClassifier.ShouldRetryStatus(resp.HttpStatus, policy.RetryClass))
                {
                    return resp;
                }
                lastResp = resp;
                lastExc = null;
                if (i + 1 >= policy.MaxAttempts) return resp;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exc)
            {
                lastExc = exc;
                lastResp = null;
                if (!RetryClassifier.ShouldRetryException(exc, policy.RetryClass)) throw;
                if (i + 1 >= policy.MaxAttempts) throw;
            }
            await Task.Delay(BackoffDelay(i, policy), cancellationToken).ConfigureAwait(false);
        }
        if (lastResp is not null) return lastResp;
        if (lastExc is not null) throw lastExc;
        throw new InvalidOperationException("Retry loop terminated without result.");
    }

    private static TimeSpan BackoffDelay(int attempt, RetryPolicy policy)
    {
        var baseSec = Math.Min(policy.BackoffFactor * Math.Pow(2, attempt), policy.BackoffMaxSeconds);
        double jitter;
        lock (_rng)
        {
            jitter = 0.5 + _rng.NextDouble();
        }
        return TimeSpan.FromSeconds(baseSec * jitter);
    }
}
