using Microsoft.AspNetCore.Http;

namespace FunctionsWebAppHost;

/// <summary>Dispatches a Functions HTTP request into the hosted ASP.NET Core application.</summary>
public interface IWebAppHost
{
    Task DispatchAsync(HttpContext context, CancellationToken cancellationToken);
}
