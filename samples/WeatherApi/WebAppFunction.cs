using FunctionsWebAppHost;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;

namespace WeatherApi;

public sealed class WebAppFunction(IWebAppHost webApp)
{
    [Function("WebApp")]
    public Task Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "{**path}")]
        HttpRequest request,
        CancellationToken cancellationToken) =>
        webApp.DispatchAsync(request.HttpContext, cancellationToken);
}
