using AudD.Internal;
using Xunit;

namespace AudD.Tests.Unit;

public class RetryPolicyTests
{
    [Fact]
    public void Read_RetriesOn500Series()
    {
        Assert.True(RetryClassifier.ShouldRetryStatus(500, RetryClass.Read));
        Assert.True(RetryClassifier.ShouldRetryStatus(502, RetryClass.Read));
        Assert.True(RetryClassifier.ShouldRetryStatus(503, RetryClass.Read));
        Assert.True(RetryClassifier.ShouldRetryStatus(429, RetryClass.Read));
        Assert.True(RetryClassifier.ShouldRetryStatus(408, RetryClass.Read));
        Assert.False(RetryClassifier.ShouldRetryStatus(200, RetryClass.Read));
        Assert.False(RetryClassifier.ShouldRetryStatus(404, RetryClass.Read));
    }

    [Fact]
    public void Recognition_RetriesOn5xxOnly()
    {
        Assert.True(RetryClassifier.ShouldRetryStatus(500, RetryClass.Recognition));
        Assert.False(RetryClassifier.ShouldRetryStatus(429, RetryClass.Recognition));
        Assert.False(RetryClassifier.ShouldRetryStatus(408, RetryClass.Recognition));
        Assert.False(RetryClassifier.ShouldRetryStatus(200, RetryClass.Recognition));
    }

    [Fact]
    public void Mutating_NeverRetriesOnStatus()
    {
        Assert.False(RetryClassifier.ShouldRetryStatus(500, RetryClass.Mutating));
        Assert.False(RetryClassifier.ShouldRetryStatus(429, RetryClass.Mutating));
        Assert.False(RetryClassifier.ShouldRetryStatus(200, RetryClass.Mutating));
    }

    [Fact]
    public void Read_RetriesOnTransportException()
    {
        Assert.True(RetryClassifier.ShouldRetryException(new HttpRequestException("net"), RetryClass.Read));
        Assert.True(RetryClassifier.ShouldRetryException(new TaskCanceledException(), RetryClass.Read));
    }

    [Fact]
    public void Recognition_RetriesOnPreUploadOnly()
    {
        // HttpRequestException is treated as pre-upload
        Assert.True(RetryClassifier.ShouldRetryException(new HttpRequestException("net"), RetryClass.Recognition));
        // TaskCanceledException = read-timeout-after-upload — DO NOT retry (cost protection)
        Assert.False(RetryClassifier.ShouldRetryException(new TaskCanceledException(), RetryClass.Recognition));
    }

    [Fact]
    public void Mutating_RetriesOnPreUploadOnly()
    {
        Assert.True(RetryClassifier.ShouldRetryException(new HttpRequestException("net"), RetryClass.Mutating));
        Assert.False(RetryClassifier.ShouldRetryException(new TaskCanceledException(), RetryClass.Mutating));
    }
}
