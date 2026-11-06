using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FunctionsWebAppHost.Internal;

internal sealed class InProcessServer(
    IServiceScopeFactory scopes,
    IHttpContextAccessor contextAccessor,
    ILogger<InProcessServer> logger) : IServer
{
    private readonly object _gate = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Func<IFeatureCollection, Task>? _process;
    private bool _accepting;
    private int _activeRequests;

    public IFeatureCollection Features { get; } = new FeatureCollection();

    public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken)
        where TContext : notnull
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_process is not null)
            {
                throw new InvalidOperationException("The application server cannot be started twice.");
            }

            _process = features => ProcessAsync(application, features);
            _accepting = true;
        }

        return Task.CompletedTask;
    }

    public async Task DispatchAsync(HttpContext transport, CancellationToken cancellationToken)
    {
        Func<IFeatureCollection, Task> process;
        lock (_gate)
        {
            if (!_accepting || _process is null)
            {
                throw new InvalidOperationException("The application server is not accepting requests.");
            }

            process = _process;
            _activeRequests++;
        }

        var previousContext = contextAccessor.HttpContext;
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                transport.RequestAborted, cancellationToken);
            await using var scope = scopes.CreateAsyncScope();
            await using var exchange = new HttpExchange(transport, scope.ServiceProvider, cancellation);
            try
            {
                await process(exchange.Features);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Web application dispatch failed for {Method} {Path}; response started: {Started}",
                    transport.Request.Method, transport.Request.Path, transport.Response.HasStarted);
                if (transport.Response.HasStarted)
                {
                    transport.Abort();
                }

                throw;
            }
        }
        finally
        {
            contextAccessor.HttpContext = previousContext;
            lock (_gate)
            {
                _activeRequests--;
                if (!_accepting && _activeRequests == 0)
                {
                    _drained.TrySetResult();
                }
            }
        }
    }

    private async Task ProcessAsync<TContext>(
        IHttpApplication<TContext> application, IFeatureCollection features) where TContext : notnull
    {
        var context = application.CreateContext(features);
        Exception? error = null;
        try
        {
            await application.ProcessRequestAsync(context);
            await features.GetRequiredFeature<IHttpResponseBodyFeature>().CompleteAsync();
        }
        catch (Exception exception)
        {
            error = exception;
            throw;
        }
        finally
        {
            try
            {
                await ((HttpExchange.ResponseFeature)features.GetRequiredFeature<IHttpResponseFeature>())
                    .FireCompletedAsync();
            }
            catch (Exception callbackError) when (error is not null)
            {
                logger.LogError(callbackError, "Completion callback failed while handling a request failure.");
            }
            finally
            {
                application.DisposeContext(context, error);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _accepting = false;
            if (_activeRequests == 0)
            {
                _drained.TrySetResult();
            }
        }

        return _drained.Task.WaitAsync(cancellationToken);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _accepting = false;
        }
    }
}
