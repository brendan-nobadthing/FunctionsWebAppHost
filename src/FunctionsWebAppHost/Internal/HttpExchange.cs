using System.IO.Pipelines;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Features.Authentication;

namespace FunctionsWebAppHost.Internal;

internal sealed class HttpExchange : IAsyncDisposable
{
    private readonly ResponseBodyFeature _body;

    public HttpExchange(HttpContext transport, IServiceProvider services, CancellationTokenSource cancellation)
    {
        var response = new ResponseFeature(transport.Response);
        _body = new ResponseBodyFeature(transport.Response, response);
        response.Body = _body.Stream;
        Features = new FeatureCollection();
        Features.Set<IHttpRequestFeature>(new HttpRequestFeature
        {
            Method = transport.Request.Method,
            Scheme = transport.Request.Scheme,
            Protocol = transport.Request.Protocol,
            Path = transport.Request.Path.Value ?? "",
            PathBase = transport.Request.PathBase.Value ?? "",
            QueryString = transport.Request.QueryString.Value ?? "",
            RawTarget = transport.Features.Get<IHttpRequestFeature>()?.RawTarget
                ?? transport.Request.Path + transport.Request.QueryString,
            Headers = new HeaderDictionary(transport.Request.Headers.ToDictionary(pair => pair.Key, pair => pair.Value)),
            Body = new BorrowedStream(transport.Request.Body)
        });
        Features.Set<IHttpResponseFeature>(response);
        Features.Set<IHttpResponseBodyFeature>(_body);
        Features.Set<IHttpRequestBodyDetectionFeature>(new RequestBodyDetectionFeature(
            transport.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody
            ?? (transport.Request.ContentLength is > 0 || transport.Request.Headers.ContainsKey("Transfer-Encoding"))));
        Features.Set<IServiceProvidersFeature>(new ServicesFeature { RequestServices = services });
        Features.Set<IHttpRequestLifetimeFeature>(new LifetimeFeature(transport, cancellation));
        Features.Set<IHttpRequestIdentifierFeature>(new HttpRequestIdentifierFeature
        {
            TraceIdentifier = transport.TraceIdentifier
        });
        Features.Set<IHttpAuthenticationFeature>(new HttpAuthenticationFeature
        {
            User = new ClaimsPrincipal(transport.User.Identities.Select(identity => identity.Clone()))
        });
        Features.Set<IHttpConnectionFeature>(new HttpConnectionFeature
        {
            ConnectionId = transport.Connection.Id,
            LocalIpAddress = transport.Connection.LocalIpAddress,
            LocalPort = transport.Connection.LocalPort,
            RemoteIpAddress = transport.Connection.RemoteIpAddress,
            RemotePort = transport.Connection.RemotePort
        });
    }

    public IFeatureCollection Features { get; }
    public ValueTask DisposeAsync() => _body.DisposeAsync();

    private sealed class ServicesFeature : IServiceProvidersFeature
    {
        public IServiceProvider RequestServices { get; set; } = null!;
    }

    private sealed class RequestBodyDetectionFeature(bool canHaveBody) : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody { get; } = canHaveBody;
    }

    private sealed class LifetimeFeature(HttpContext transport, CancellationTokenSource cancellation)
        : IHttpRequestLifetimeFeature
    {
        public CancellationToken RequestAborted { get; set; } = cancellation.Token;
        public void Abort()
        {
            cancellation.Cancel();
            transport.Abort();
        }
    }

    internal sealed class ResponseFeature(HttpResponse response) : IHttpResponseFeature
    {
        private readonly Stack<(Func<object, Task> Callback, object State)> _starting = new();
        private readonly Stack<(Func<object, Task> Callback, object State)> _completed = new();
        private bool _started;

        public int StatusCode { get => response.StatusCode; set => response.StatusCode = value; }
        public string? ReasonPhrase
        {
            get => response.HttpContext.Features.GetRequiredFeature<IHttpResponseFeature>().ReasonPhrase;
            set => response.HttpContext.Features.GetRequiredFeature<IHttpResponseFeature>().ReasonPhrase = value;
        }
        public IHeaderDictionary Headers
        {
            get => response.Headers;
            set => response.HttpContext.Features.GetRequiredFeature<IHttpResponseFeature>().Headers = value;
        }
        public Stream Body { get; set; } = Stream.Null;
        public bool HasStarted => response.HasStarted;

        public void OnStarting(Func<object, Task> callback, object state)
        {
            if (_started || HasStarted)
            {
                throw new InvalidOperationException("The response has already started.");
            }
            _starting.Push((callback, state));
        }

        public void OnCompleted(Func<object, Task> callback, object state) => _completed.Push((callback, state));

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (_started)
            {
                return;
            }
            while (_starting.TryPop(out var callback))
            {
                await callback.Callback(callback.State);
            }
            await response.StartAsync(cancellationToken);
            _started = true;
        }

        public async Task FireCompletedAsync()
        {
            List<Exception>? errors = null;
            while (_completed.TryPop(out var callback))
            {
                try
                {
                    await callback.Callback(callback.State);
                }
                catch (Exception exception)
                {
                    (errors ??= []).Add(exception);
                }
            }
            if (errors is not null)
            {
                throw new AggregateException("Application completion callbacks failed.", errors);
            }
        }
    }

    private sealed class ResponseBodyFeature(HttpResponse response, ResponseFeature feature)
        : IHttpResponseBodyFeature, IAsyncDisposable
    {
        private PipeWriter? _writer;
        private bool _complete;
        public Stream Stream { get; } = new ResponseStream(response.Body, feature);
        public PipeWriter Writer => _writer ??= PipeWriter.Create(Stream, new StreamPipeWriterOptions(leaveOpen: true));
        public void DisableBuffering() =>
            response.HttpContext.Features.GetRequiredFeature<IHttpResponseBodyFeature>().DisableBuffering();
        public Task StartAsync(CancellationToken cancellationToken = default) => feature.StartAsync(cancellationToken);

        public async Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default)
        {
            await StartAsync(cancellationToken);
            await response.HttpContext.Features.GetRequiredFeature<IHttpResponseBodyFeature>()
                .SendFileAsync(path, offset, count, cancellationToken);
        }

        public async Task CompleteAsync()
        {
            if (_complete)
            {
                return;
            }
            if (_writer is not null)
            {
                await _writer.CompleteAsync();
            }
            await StartAsync();
            await response.CompleteAsync();
            _complete = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_complete && _writer is not null)
            {
                await _writer.CompleteAsync(new OperationCanceledException("The application response did not complete."));
            }
        }
    }

    private class BorrowedStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.WriteAsync(buffer, offset, count, cancellationToken);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);

        // The Functions transport owns the underlying stream.
        protected override void Dispose(bool disposing) { }
    }

    private sealed class ResponseStream(Stream inner, ResponseFeature feature) : BorrowedStream(inner)
    {
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("Use asynchronous response writes.");
        public override void Flush() => throw new NotSupportedException("Use asynchronous response flushing.");

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await feature.StartAsync(cancellationToken);
            await base.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await feature.StartAsync(cancellationToken);
            await base.WriteAsync(buffer, cancellationToken);
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await feature.StartAsync(cancellationToken);
            await base.FlushAsync(cancellationToken);
        }
    }
}
