using System.Net.Http.Headers;

namespace AudD.Internal;

/// <summary>
/// Recognition source — URL, file path, byte buffer, or stream — with a
/// per-attempt re-opener that yields fresh request bodies on each retry.
/// (HttpClient does NOT auto-rewind streams.)
/// </summary>
internal abstract class RecognitionSource
{
    /// <summary>
    /// Build the multipart form for the next attempt. Caller disposes the
    /// returned <see cref="HttpContent"/>. <paramref name="extraFields"/>
    /// is a copy of the request fields the caller wants merged in.
    /// </summary>
    public abstract HttpContent BuildContent(IDictionary<string, string> extraFields);

    /// <summary>True if this source is a URL (no multipart upload).</summary>
    public virtual bool IsUrl => false;

    /// <summary>The URL string for URL sources, else null.</summary>
    public virtual string? UrlValue => null;

    public static RecognitionSource From(string urlOrPath)
    {
        if (urlOrPath is null) throw new ArgumentNullException(nameof(urlOrPath));
        if (urlOrPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || urlOrPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return new UrlSource(urlOrPath);
        }
        if (!File.Exists(urlOrPath))
        {
            throw new ArgumentException(
                $"'{urlOrPath}' is not an HTTP URL (must start with http:// or https://) " +
                "and is not an existing file path. Pass a URL, a FileInfo, a byte[], or a Stream.",
                nameof(urlOrPath));
        }
        return new FileSource(new FileInfo(urlOrPath));
    }

    public static RecognitionSource From(FileInfo file)
    {
        if (file is null) throw new ArgumentNullException(nameof(file));
        if (!file.Exists) throw new FileNotFoundException("File not found.", file.FullName);
        return new FileSource(file);
    }

    public static RecognitionSource From(byte[] bytes)
    {
        if (bytes is null) throw new ArgumentNullException(nameof(bytes));
        return new BytesSource(bytes);
    }

    public static RecognitionSource From(System.IO.Stream stream)
    {
        if (stream is null) throw new ArgumentNullException(nameof(stream));
        return new StreamSource(stream);
    }

    private sealed class UrlSource : RecognitionSource
    {
        private readonly string _url;
        public UrlSource(string url) { _url = url; }
        public override bool IsUrl => true;
        public override string? UrlValue => _url;

        public override HttpContent BuildContent(IDictionary<string, string> extraFields)
        {
            var form = new MultipartFormDataContent();
            form.Add(new StringContent(_url), "url");
            foreach (var kv in extraFields)
            {
                form.Add(new StringContent(kv.Value), kv.Key);
            }
            return form;
        }
    }

    private sealed class FileSource : RecognitionSource
    {
        private readonly FileInfo _file;
        public FileSource(FileInfo f) { _file = f; }

        public override HttpContent BuildContent(IDictionary<string, string> extraFields)
        {
            var form = new MultipartFormDataContent();
            // Open a fresh handle each attempt — owned by the StreamContent and disposed by the form.
            var fs = _file.OpenRead();
            var sc = new StreamContent(fs);
            sc.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(sc, "file", _file.Name);
            foreach (var kv in extraFields)
            {
                form.Add(new StringContent(kv.Value), kv.Key);
            }
            return form;
        }
    }

    private sealed class BytesSource : RecognitionSource
    {
        private readonly byte[] _bytes;
        public BytesSource(byte[] b) { _bytes = b; }

        public override HttpContent BuildContent(IDictionary<string, string> extraFields)
        {
            var form = new MultipartFormDataContent();
            var bc = new ByteArrayContent(_bytes);
            bc.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(bc, "file", "upload.bin");
            foreach (var kv in extraFields)
            {
                form.Add(new StringContent(kv.Value), kv.Key);
            }
            return form;
        }
    }

    private sealed class StreamSource : RecognitionSource
    {
        private readonly System.IO.Stream _stream;
        private readonly bool _seekable;
        private readonly long _start;
        private bool _firstCall = true;

        public StreamSource(System.IO.Stream s)
        {
            _stream = s;
            _seekable = s.CanSeek;
            _start = _seekable ? s.Position : 0;
        }

        public override HttpContent BuildContent(IDictionary<string, string> extraFields)
        {
            if (_firstCall)
            {
                _firstCall = false;
            }
            else
            {
                if (!_seekable)
                {
                    throw new InvalidOperationException(
                        "Cannot retry an unseekable Stream source. Buffer the content into a byte[] " +
                        "or use a FileInfo / URL.");
                }
                _stream.Seek(_start, SeekOrigin.Begin);
            }

            var form = new MultipartFormDataContent();
            // Wrap the user's stream so MultipartFormDataContent.Dispose doesn't dispose it.
            var wrap = new NonDisposingStreamWrapper(_stream);
            var sc = new StreamContent(wrap);
            sc.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(sc, "file", "upload.bin");
            foreach (var kv in extraFields)
            {
                form.Add(new StringContent(kv.Value), kv.Key);
            }
            return form;
        }
    }

    /// <summary>
    /// Wraps a stream so disposing this wrapper does NOT dispose the inner stream.
    /// Used for caller-supplied streams the SDK does not own.
    /// </summary>
    private sealed class NonDisposingStreamWrapper : System.IO.Stream
    {
        private readonly System.IO.Stream _inner;
        public NonDisposingStreamWrapper(System.IO.Stream inner) { _inner = inner; }
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        protected override void Dispose(bool disposing)
        {
            // Deliberately NOT disposing _inner. The caller owns it.
        }
    }
}
