using FunctionsWebAppHost.Internal;
using Microsoft.AspNetCore.Builder;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FunctionsWebAppHost;

public static class FunctionsApplicationBuilderExtensions
{
    /// <summary>
    /// Registers one in-process web application host. Configures its builder before Build,
    /// and its middleware and endpoints after Build but before StartAsync.
    /// </summary>
    public static FunctionsApplicationBuilder HostWebApp(
        this FunctionsApplicationBuilder builder,
        Action<WebApplicationBuilder> configureBuilder,
        Action<WebApplication> configureApplication)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configureBuilder);
        ArgumentNullException.ThrowIfNull(configureApplication);
        if (builder.Services.Any(service => service.ServiceType == typeof(WebAppRegistration)))
        {
            throw new InvalidOperationException("HostWebApp supports only one application per Functions worker.");
        }

        builder.ConfigureFunctionsWebApplication();
        builder.Services.AddSingleton(new WebAppRegistration(configureBuilder, configureApplication));
        builder.Services.AddSingleton<ApplicationHost>();
        builder.Services.AddSingleton<IWebAppHost>(services => services.GetRequiredService<ApplicationHost>());
        builder.Services.AddHostedService(services => services.GetRequiredService<ApplicationHost>());
        return builder;
    }
}
