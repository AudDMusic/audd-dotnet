using System.Text;
using AudD.Internal;
using Xunit;

namespace AudD.Tests.Unit;

public class SourceTests
{
    [Fact]
    public void From_HttpsString_BuildsUrlSource()
    {
        var s = RecognitionSource.From("https://audd.tech/example.mp3");
        Assert.True(s.IsUrl);
        Assert.Equal("https://audd.tech/example.mp3", s.UrlValue);
    }

    [Fact]
    public void From_HttpString_BuildsUrlSource()
    {
        var s = RecognitionSource.From("http://x.example/y.mp3");
        Assert.True(s.IsUrl);
    }

    [Fact]
    public void From_Bytes_BuildsBytesSource()
    {
        var s = RecognitionSource.From(new byte[] { 1, 2, 3 });
        Assert.False(s.IsUrl);
        using var content = s.BuildContent(new Dictionary<string, string>());
        Assert.IsType<MultipartFormDataContent>(content);
    }

    [Fact]
    public void From_NonExistentString_ThrowsHelpfulError()
    {
        var ex = Assert.Throws<ArgumentException>(() => RecognitionSource.From("does-not-exist.mp3"));
        Assert.Contains("URL", ex.Message);
    }

    [Fact]
    public void From_Stream_RetryRequiresSeekable()
    {
        var data = Encoding.UTF8.GetBytes("hello");
        // Use a wrapper that disallows seeking
        using var unseekable = new NonSeekableStream(new MemoryStream(data));
        var s = RecognitionSource.From(unseekable);
        // First call OK
        using (s.BuildContent(new Dictionary<string, string>())) { /* drained */ }
        // Second call must throw
        Assert.Throws<InvalidOperationException>(() => s.BuildContent(new Dictionary<string, string>()));
    }

    [Fact]
    public void From_Stream_SeekableSurvivesRetry()
    {
        var data = Encoding.UTF8.GetBytes("hello");
        using var ms = new MemoryStream(data);
        var s = RecognitionSource.From(ms);
        using (s.BuildContent(new Dictionary<string, string>())) { }
        // Second call must succeed for seekable
        using (s.BuildContent(new Dictionary<string, string>())) { }
    }

    [Fact]
    public void From_File_ReopensEachAttempt()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tmp, new byte[] { 1, 2, 3 });
            var s = RecognitionSource.From(new FileInfo(tmp));
            using (s.BuildContent(new Dictionary<string, string>())) { }
            using (s.BuildContent(new Dictionary<string, string>())) { }
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    private sealed class NonSeekableStream : System.IO.Stream
    {
        private readonly System.IO.Stream _inner;
        public NonSeekableStream(System.IO.Stream inner) { _inner = inner; }
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }
}
