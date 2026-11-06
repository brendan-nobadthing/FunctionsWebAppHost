using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FunctionsWebAppHost.Internal;

internal sealed class ApplicationHost(
    WebAppRegistration registration,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<ApplicationHost> logger) : IWebAppHost, IHostedService, IAsyncDisposable
{
    private WebApplication? _application;
    private InProcessServer? _server;
    private int _startAttempted;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _startAttempted, 1) != 0)
        {
            throw new InvalidOperationException("The hosted web application cannot be started twice.");
        }

        WebApplication? application = null;
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = environment.ApplicationName,
                EnvironmentName = environment.EnvironmentName,
                ContentRootPath = environment.ContentRootPath,
                Args = []
            });
            builder.Configuration.AddConfiguration(configuration);
            registration.ConfigureBuilder(builder);
            ConfigureHosting(builder.Services);
            application = builder.Build();
            registration.ConfigureApplication(application);
            await application.StartAsync(cancellationToken);
            _application = application;
            _server = application.Services.GetRequiredService<InProcessServer>();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "The hosted web application failed to configure or start.");
            if (application is not null)
            {
                try
                {
                    await application.DisposeAsync();
                }
                catch (Exception disposalException)
                {
                    logger.LogError(disposalException, "Cleanup of the failed web application host also failed.");
                }
            }
            throw;
        }
    }

    private static void ConfigureHosting(IServiceCollection services)
    {
        // These belong to the inner application, never the worker's HTTP ingress.
        services.RemoveAll<IServer>();
        services.RemoveAll<InProcessServer>();
        services.RemoveAll<IHttpContextAccessor>();
        services.AddSingleton<IHttpContextAccessor, ApplicationHttpContextAccessor>();
        services.AddSingleton<InProcessServer>();
        services.AddSingleton<IServer>(provider => provider.GetRequiredService<InProcessServer>());
    }

    public Task DispatchAsync(HttpContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return (_server ?? throw new InvalidOperationException("The hosted web application has not started."))
            .DispatchAsync(context, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        _application?.StopAsync(cancellationToken) ?? Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        var application = Interlocked.Exchange(ref _application, null);
        _server = null;
        if (application is not null)
        {
            await application.DisposeAsync();
        }
    }
}
