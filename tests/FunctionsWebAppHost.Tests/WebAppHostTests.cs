using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FunctionsWebAppHost;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FunctionsWebAppHost.Tests;

public sealed class WebAppHostTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfiguresOnceAndKeepsInnerHostingSeparate(bool alreadyConfigured)
    {
        var builder = CreateBuilder();
        if (alreadyConfigured)
        {
            builder.ConfigureFunctionsWebApplication();
        }
        builder.Configuration["Probe"] = "worker";
        builder.Services.AddSingleton(new WorkerOnlyService());
        var callbacks = new List<string>();
        IServer? innerServer = null;
        builder.HostWebApp(
            web =>
            {
                callbacks.Add("builder");
                Assert.Equal("worker", web.Configuration["Probe"]);
                Assert.Equal(builder.Environment.ApplicationName, web.Environment.ApplicationName);
                web.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Probe"] = "application" });
                web.Services.AddSingleton(new ApplicationOnlyService());
                web.Services.AddSingleton<IServer, RejectingServer>();
                web.Services.AddHttpContextAccessor();
            },
            app =>
            {
                callbacks.Add("pipeline");
                Assert.Equal("application", app.Configuration["Probe"]);
                Assert.NotNull(app.Services.GetRequiredService<ApplicationOnlyService>());
                Assert.Null(app.Services.GetService<WorkerOnlyService>());
                innerServer = app.Services.GetRequiredService<IServer>();
                Assert.IsNotType<RejectingServer>(innerServer);
                app.MapGet("/", (HttpContext context, IHttpContextAccessor accessor) =>
                {
                    Assert.Same(context, accessor.HttpContext);
                    return TypedResults.Text("configured");
                });
            });

        await using var fixture = new AsyncWorkerOwner(builder.Build());
        Assert.Empty(callbacks);
        var dispatcher = fixture.Worker.Services.GetRequiredService<IWebAppHost>();
        var lifetime = Assert.IsAssignableFrom<IHostedService>(dispatcher);
        var hostedRegistration = builder.Services.Last(service => service.ServiceType == typeof(IHostedService));
        Assert.NotNull(hostedRegistration.ImplementationFactory);
        Assert.Same(dispatcher, hostedRegistration.ImplementationFactory(fixture.Worker.Services));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            dispatcher.DispatchAsync(CreateTransport("/"), CancellationToken.None));
        await lifetime.StartAsync(CancellationToken.None);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => lifetime.StartAsync(CancellationToken.None));
            var outerServer = fixture.Worker.Services.GetRequiredService<IServer>();
            Assert.NotSame(innerServer, outerServer);
            Assert.Null(fixture.Worker.Services.GetService<ApplicationOnlyService>());
            var context = CreateTransport("/");
            var outerAccessor = new HttpContextAccessor { HttpContext = context };
            await dispatcher.DispatchAsync(context, CancellationToken.None);
            Assert.Same(context, outerAccessor.HttpContext);
            outerAccessor.HttpContext = null;
            Assert.Equal("configured", ReadBody(context));
            Assert.Equal(new[] { "builder", "pipeline" }, callbacks);
        }
        finally
        {
            await lifetime.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task StartupCleanupFailureDoesNotReplaceConfigurationFailure()
    {
        var builder = CreateBuilder();
        builder.HostWebApp(
            web => web.Services.AddSingleton(_ => new FailingDisposal()),
            app =>
            {
                app.Services.GetRequiredService<FailingDisposal>();
                throw new InvalidOperationException("configuration failure");
            });
        await using var fixture = new AsyncWorkerOwner(builder.Build());
        var lifetime = Assert.IsAssignableFrom<IHostedService>(
            fixture.Worker.Services.GetRequiredService<IWebAppHost>());
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => lifetime.StartAsync(CancellationToken.None));
        Assert.Equal("configuration failure", failure.Message);
    }

    [Fact]
    public void RejectsRepeatedRegistrationAndMissingCallbacks()
    {
        var builder = CreateBuilder();
        Assert.Throws<ArgumentNullException>(() => builder.HostWebApp(null!, _ => { }));
        Assert.Throws<ArgumentNullException>(() => builder.HostWebApp(_ => { }, null!));
        builder.HostWebApp(_ => { }, _ => { });
        Assert.Throws<InvalidOperationException>(() => builder.HostWebApp(_ => { }, _ => { }));
    }

    [Theory]
    [InlineData("builder")]
    [InlineData("pipeline")]
    [InlineData("hosted-service")]
    public async Task StartupFailureIsVisibleAndBuiltResourcesAreDisposed(string stage)
    {
        var builder = CreateBuilder();
        var resource = new OwnedResource();
        builder.HostWebApp(
            web =>
            {
                if (stage == "builder")
                {
                    throw new InvalidOperationException("builder failure");
                }
                web.Services.AddSingleton(_ => resource);
                if (stage == "hosted-service")
                {
                    web.Services.AddHostedService<FailingHostedService>();
                }
            },
            app =>
            {
                app.Services.GetRequiredService<OwnedResource>();
                if (stage == "pipeline")
                {
                    throw new InvalidOperationException("pipeline failure");
                }
            });
        await using var fixture = new AsyncWorkerOwner(builder.Build());
        var dispatcher = fixture.Worker.Services.GetRequiredService<IWebAppHost>();
        var lifetime = Assert.IsAssignableFrom<IHostedService>(dispatcher);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => lifetime.StartAsync(CancellationToken.None));
        Assert.Contains(stage, error.Message);
        if (stage != "builder")
        {
            Assert.Equal(1, resource.Disposals);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            dispatcher.DispatchAsync(CreateTransport("/"), CancellationToken.None));
    }

    [Fact]
    public async Task DiscoversConsumerControllersAndBindsJson()
    {
        await using var fixture = await ApplicationFixture.StartAsync(
            web => web.Services.AddControllers(),
            app => app.MapControllers());
        var context = CreateTransport("/controller/42", "?message=hello%20world");
        var outerEndpoint = new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(), "function");
        context.SetEndpoint(outerEndpoint);
        context.Request.RouteValues["path"] = "outer";
        await fixture.Dispatcher.DispatchAsync(context, CancellationToken.None);
        Assert.Equal("""{"id":42,"message":"hello world"}""", ReadBody(context));
        Assert.Same(outerEndpoint, context.GetEndpoint());
        Assert.Equal("outer", context.Request.RouteValues["path"]);

        var post = CreateTransport("/controller/echo");
        SetJsonBody(post, """{"message":"controller body"}""");
        await fixture.Dispatcher.DispatchAsync(post, CancellationToken.None);
        Assert.Equal("""{"message":"controller body"}""", ReadBody(post));
    }

    [Theory]
    [InlineData("content-length")]
    [InlineData("transfer-encoding")]
    [InlineData("transport-feature")]
    public async Task MinimalOnlyApplicationBindsBodyWithoutMvcRegistration(string bodyDetection)
    {
        await using var fixture = await ApplicationFixture.StartAsync(
            _ => { },
            app => app.MapPost("/echo", (EchoRequest request) => TypedResults.Ok(request)));
        var context = CreateTransport("/echo");
        SetJsonBody(context, """{"message":"minimal body"}""");
        if (bodyDetection != "content-length")
        {
            context.Request.ContentLength = null;
            if (bodyDetection == "transfer-encoding")
            {
                context.Request.Headers.TransferEncoding = "chunked";
            }
            else
            {
                context.Features.Set<IHttpRequestBodyDetectionFeature>(new TestBodyDetectionFeature());
            }
        }
        await fixture.Dispatcher.DispatchAsync(context, CancellationToken.None);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("""{"message":"minimal body"}""", ReadBody(context));
    }

    [Fact]
    public async Task PreservesRequestMetadataAndRepeatedHeadersWithoutLeakingOuterScope()
    {
        await using var fixture = await ApplicationFixture.StartAsync(
            _ => { },
            app => app.MapGet("/inspect/{**path}", context => context.Response.WriteAsJsonAsync(new
            {
                Method = context.Request.Method,
                RawTarget = context.Features.GetRequiredFeature<IHttpRequestFeature>().RawTarget,
                Protocol = context.Request.Protocol,
                Scheme = context.Request.Scheme,
                Host = context.Request.Host.Value,
                PathBase = context.Request.PathBase.Value,
                Path = context.Request.Path.Value,
                QueryString = context.Request.QueryString.Value,
                Query = context.Request.Query["value"].ToArray(),
                Headers = context.Request.Headers["X-Probe"].ToArray(),
                Cookie = context.Request.Cookies["probe"],
                RemoteIp = context.Connection.RemoteIpAddress?.ToString(),
                Subject = context.User.FindFirstValue("sub"),
                Trace = context.TraceIdentifier,
                OuterItem = context.Items.ContainsKey("worker")
            })));
        var context = CreateTransport("/inspect/a%2Fb", "?value=one&value=two&escaped=a%2Bb");
        context.Features.GetRequiredFeature<IHttpRequestFeature>().RawTarget = "/base/inspect/a%2Fb?value=one&value=two&escaped=a%2Bb";
        context.Request.Protocol = "HTTP/1.1";
        context.Request.PathBase = "/base";
        context.Request.Headers.Append("X-Probe", new[] { "first", "second" });
        context.Request.Headers.Cookie = "probe=cookie-value";
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");
        context.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", "probe-user") }, "test"));
        context.TraceIdentifier = "outer-trace";
        context.Items["worker"] = "private";
        await fixture.Dispatcher.DispatchAsync(context, CancellationToken.None);
        using var document = JsonDocument.Parse(ReadBody(context));
        var value = document.RootElement;
        Assert.Equal("GET", value.GetProperty("method").GetString());
        Assert.Equal("/base/inspect/a%2Fb?value=one&value=two&escaped=a%2Bb", value.GetProperty("rawTarget").GetString());
        Assert.Equal("HTTP/1.1", value.GetProperty("protocol").GetString());
        Assert.Equal("https", value.GetProperty("scheme").GetString());
        Assert.Equal("adapter.example", value.GetProperty("host").GetString());
        Assert.Equal("/base", value.GetProperty("pathBase").GetString());
        Assert.Equal("/inspect/a%2Fb", value.GetProperty("path").GetString());
        Assert.Equal("?value=one&value=two&escaped=a%2Bb", value.GetProperty("queryString").GetString());
        Assert.Equal(new[] { "one", "two" }, value.GetProperty("query").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(new[] { "first", "second" }, value.GetProperty("headers").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal("cookie-value", value.GetProperty("cookie").GetString());
        Assert.Equal("192.0.2.10", value.GetProperty("remoteIp").GetString());
        Assert.Equal("probe-user", value.GetProperty("subject").GetString());
        Assert.Equal("outer-trace", value.GetProperty("trace").GetString());
        Assert.False(value.GetProperty("outerItem").GetBoolean());
    }

    [Fact]
    public async Task ConcurrentRequestsHaveDistinctScopesAndCorrectCallbackDisposalOrder()
    {
        var events = new ConcurrentDictionary<string, ConcurrentQueue<string>>();
        await using var fixture = await ApplicationFixture.StartAsync(
            web => web.Services.AddScoped<ScopeProbe>(),
            app =>
            {
                app.Use(async (context, next) =>
                {
                    var scope = context.RequestServices.GetRequiredService<ScopeProbe>();
                    events[scope.Id] = scope.Events;
                    context.Response.OnStarting(() =>
                    {
                        scope.Events.Enqueue("starting");
                        context.Response.Headers["X-Scope"] = scope.Id;
                        return Task.CompletedTask;
                    });
                    context.Response.OnCompleted(() =>
                    {
                        scope.Events.Enqueue("completed");
                        return Task.CompletedTask;
                    });
                    await next(context);
                });
                app.MapGet("/scope", async (HttpContext context, ScopeProbe scope, IHttpContextAccessor accessor) =>
                {
                    Assert.Same(context, accessor.HttpContext);
                    Assert.Same(scope, context.RequestServices.GetRequiredService<ScopeProbe>());
                    context.Response.Cookies.Append("first", "1");
                    context.Response.Cookies.Append("second", "2");
                    await Task.Delay(10, context.RequestAborted);
                    await context.Response.WriteAsync(scope.Id, context.RequestAborted);
                });
            });
        var contexts = Enumerable.Range(0, 20).Select(_ => CreateTransport("/scope")).ToArray();
        await Task.WhenAll(contexts.Select(context => fixture.Dispatcher.DispatchAsync(context, CancellationToken.None)));
        Assert.Equal(20, contexts.Select(context => ReadBody(context)).Distinct().Count());
        Assert.All(contexts, context =>
        {
            var id = ReadBody(context);
            Assert.Equal(id, context.Response.Headers["X-Scope"].ToString());
            Assert.Equal(new[] { "starting", "completed", "disposed" }, events[id].ToArray());
            Assert.Equal("second=2; path=/", context.Response.Headers.SetCookie[1]);
            Assert.True(context.Response.Body.CanWrite);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureIsPropagatedAndCleanupDoesNotMaskIt(bool afterStart)
    {
        ScopeProbe? probe = null;
        await using var fixture = await ApplicationFixture.StartAsync(
            web => web.Services.AddScoped<ScopeProbe>(),
            app => app.MapGet("/failure", async context =>
            {
                probe = context.RequestServices.GetRequiredService<ScopeProbe>();
                context.Response.OnCompleted(() => throw new InvalidOperationException("callback failure"));
                if (afterStart)
                {
                    await context.Response.WriteAsync("started", context.RequestAborted);
                }
                throw new InvalidOperationException("request failure");
            }));
        var context = CreateTransport("/failure");
        var lifetime = new TestLifetimeFeature();
        context.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Dispatcher.DispatchAsync(context, CancellationToken.None));
        Assert.Equal("request failure", exception.Message);
        Assert.Equal(afterStart, lifetime.Aborted);
        Assert.NotNull(probe);
        Assert.Equal("disposed", probe.Events.Last());
        Assert.True(context.Response.Body.CanWrite);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestOrInvocationCancellationReachesApplication(bool invocationCancellation)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ScopeProbe? probe = null;
        await using var fixture = await ApplicationFixture.StartAsync(
            web => web.Services.AddScoped<ScopeProbe>(),
            app => app.MapGet("/cancel", async context =>
            {
                probe = context.RequestServices.GetRequiredService<ScopeProbe>();
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, context.RequestAborted);
            }));
        var context = CreateTransport("/cancel");
        using var cancellation = new CancellationTokenSource();
        if (!invocationCancellation)
        {
            context.RequestAborted = cancellation.Token;
        }
        var dispatch = fixture.Dispatcher.DispatchAsync(context,
            invocationCancellation ? cancellation.Token : CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatch);
        Assert.NotNull(probe);
        Assert.Equal("disposed", probe.Events.Last());
    }

    [Fact]
    public async Task ApplicationSelectsItsOwnMiddlewareAndErrorPolicy()
    {
        var order = new List<string>();
        await using var fixture = await ApplicationFixture.StartAsync(
            _ => { },
            app =>
            {
                app.Use(async (context, next) =>
                {
                    order.Add("outer-before");
                    try
                    {
                        await next(context);
                    }
                    catch (InvalidOperationException)
                    {
                        context.Response.StatusCode = 409;
                        await context.Response.WriteAsync("consumer policy");
                    }
                    order.Add("outer-after");
                });
                app.Use(async (context, next) =>
                {
                    order.Add("inner");
                    await next(context);
                });
                app.MapGet("/error", (RequestDelegate)(_ => throw new InvalidOperationException("test")));
            });
        var context = CreateTransport("/error");
        await fixture.Dispatcher.DispatchAsync(context, CancellationToken.None);
        Assert.Equal(409, context.Response.StatusCode);
        Assert.Equal("consumer policy", ReadBody(context));
        Assert.Equal(new[] { "outer-before", "inner", "outer-after" }, order);
    }

    [Fact]
    public async Task BodyWriterStreamsBeforeRequestProducerCompletesAndBorrowedStreamsStayOpen()
    {
        await using var fixture = await ApplicationFixture.StartAsync(
            _ => { },
            app => app.MapPost("/duplex", async context =>
            {
                var buffer = new byte[128];
                int count;
                while ((count = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
                {
                    await context.Response.BodyWriter.WriteAsync(buffer.AsMemory(0, count), context.RequestAborted);
                    await context.Response.BodyWriter.FlushAsync(context.RequestAborted);
                }
            }));
        var context = CreateTransport("/duplex");
        context.Request.Method = "POST";
        var pipe = new Pipe();
        context.Request.Body = pipe.Reader.AsStream();
        var output = new ObservedStream();
        context.Features.Set<IHttpResponseBodyFeature>(new TestBodyFeature(
            output, (TestResponseFeature)context.Features.GetRequiredFeature<IHttpResponseFeature>()));
        var dispatch = fixture.Dispatcher.DispatchAsync(context, CancellationToken.None);
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes("first\n"));
        await output.Written.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(dispatch.IsCompleted);
        Assert.Equal("first\n", Encoding.UTF8.GetString(output.ToArray()));
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes("second\n"));
        await pipe.Writer.CompleteAsync();
        await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("first\nsecond\n", Encoding.UTF8.GetString(output.ToArray()));
        Assert.True(output.CanWrite);
        Assert.True(context.Request.Body.CanRead);
        await pipe.Reader.CompleteAsync();
    }

    [Fact]
    public async Task StopDrainsRequestsAndDisposesSingletonOnce()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resource = new OwnedResource();
        var fixture = await ApplicationFixture.StartAsync(
            web => web.Services.AddSingleton(_ => resource),
            app =>
            {
                app.Services.GetRequiredService<OwnedResource>();
                app.MapGet("/hold", async context =>
                {
                    entered.TrySetResult();
                    await release.Task;
                    await context.Response.WriteAsync("released");
                });
            });
        try
        {
            var dispatch = fixture.Dispatcher.DispatchAsync(CreateTransport("/hold"), CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stop = fixture.Lifetime.StopAsync(CancellationToken.None);
            Assert.False(stop.IsCompleted);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Dispatcher.DispatchAsync(CreateTransport("/hold"), CancellationToken.None));
            release.TrySetResult();
            await Task.WhenAll(dispatch, stop).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.TrySetResult();
            await fixture.DisposeAsync();
        }
        Assert.Equal(1, resource.Disposals);
    }

    private static FunctionsApplicationBuilder CreateBuilder()
    {
        var builder = FunctionsApplication.CreateBuilder([]);
        builder.Environment.ApplicationName = typeof(ConsumerController).Assembly.GetName().Name!;
        builder.Environment.EnvironmentName = Environments.Production;
        builder.Logging.ClearProviders();
        return builder;
    }

    private static DefaultHttpContext CreateTransport(string path, string query = "")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("adapter.example");
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);
        context.Request.Body = new MemoryStream();
        var response = new TestResponseFeature();
        context.Features.Set<IHttpResponseFeature>(response);
        context.Features.Set<IHttpResponseBodyFeature>(new TestBodyFeature(new MemoryStream(), response));
        return context;
    }

    private static void SetJsonBody(HttpContext context, string json)
    {
        context.Request.Method = "POST";
        context.Request.ContentType = "application/json";
        var body = Encoding.UTF8.GetBytes(json);
        context.Request.Body = new MemoryStream(body);
        context.Request.ContentLength = body.Length;
    }

    private static string ReadBody(HttpContext context) =>
        Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());

    private sealed class ApplicationFixture : IAsyncDisposable
    {
        private readonly IHost _worker;
        public IWebAppHost Dispatcher { get; }
        public IHostedService Lifetime { get; }

        private ApplicationFixture(IHost worker)
        {
            _worker = worker;
            Dispatcher = worker.Services.GetRequiredService<IWebAppHost>();
            Lifetime = Assert.IsAssignableFrom<IHostedService>(Dispatcher);
        }

        public static async Task<ApplicationFixture> StartAsync(
            Action<WebApplicationBuilder> configureBuilder, Action<WebApplication> configureApplication)
        {
            var builder = CreateBuilder();
            builder.HostWebApp(configureBuilder, configureApplication);
            var fixture = new ApplicationFixture(builder.Build());
            try
            {
                await fixture.Lifetime.StartAsync(CancellationToken.None);
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Lifetime.StopAsync(CancellationToken.None);
            if (_worker is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync();
            }
            else
            {
                _worker.Dispose();
            }
        }
    }

    private sealed class AsyncWorkerOwner(IHost worker) : IAsyncDisposable
    {
        public IHost Worker { get; } = worker;
        public async ValueTask DisposeAsync()
        {
            if (Worker is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync();
            }
            else
            {
                Worker.Dispose();
            }
        }
    }

    private sealed class WorkerOnlyService;
    private sealed class ApplicationOnlyService;

    private sealed class OwnedResource : IDisposable
    {
        public int Disposals { get; private set; }
        public void Dispose() => Disposals++;
    }

    private sealed class FailingHostedService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("hosted-service failure");
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FailingDisposal : IDisposable
    {
        public void Dispose() => throw new InvalidOperationException("cleanup failure");
    }

    private sealed class ScopeProbe : IAsyncDisposable
    {
        public string Id { get; } = Guid.NewGuid().ToString();
        public ConcurrentQueue<string> Events { get; } = new();
        public ValueTask DisposeAsync()
        {
            Events.Enqueue("disposed");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RejectingServer : IServer
    {
        public IFeatureCollection Features { get; } = new FeatureCollection();
        public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken)
            where TContext : notnull => throw new InvalidOperationException("Custom server should have been replaced.");
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose() { }
    }

    private sealed class TestResponseFeature : HttpResponseFeature
    {
        public bool Started { get; set; }
        public override bool HasStarted => Started;
    }

    private sealed class TestBodyFeature(Stream stream, TestResponseFeature response) : IHttpResponseBodyFeature
    {
        private readonly StreamResponseBodyFeature _inner = new(stream);
        public Stream Stream => _inner.Stream;
        public PipeWriter Writer => _inner.Writer;
        public void DisableBuffering() => _inner.DisableBuffering();
        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            response.Started = true;
            await _inner.StartAsync(cancellationToken);
        }
        public Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default) =>
            _inner.SendFileAsync(path, offset, count, cancellationToken);
        public Task CompleteAsync() => _inner.CompleteAsync();
    }

    private sealed class TestLifetimeFeature : IHttpRequestLifetimeFeature
    {
        public bool Aborted { get; private set; }
        public CancellationToken RequestAborted { get; set; }
        public void Abort() => Aborted = true;
    }

    private sealed class TestBodyDetectionFeature : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    private sealed class ObservedStream : MemoryStream
    {
        public TaskCompletionSource Written { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await base.WriteAsync(buffer, cancellationToken);
            Written.TrySetResult();
        }
    }
}

[ApiController]
[Route("controller")]
public sealed class ConsumerController : ControllerBase
{
    [HttpGet("{id:int}")]
    public IActionResult Get(int id, [FromQuery] string? message) => Ok(new { id, message });

    [HttpPost("echo")]
    public IActionResult Echo([FromBody] EchoRequest request) => Ok(request);
}

public sealed record EchoRequest(string Message);
