using FunctionsWebAppHost;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;

namespace FunctionsWebAppHostTest;

public sealed class WebAppFunction(IWebAppHost webApp)
{
    [Function("WebApp")]
    public Task Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", "put", "patch",
            "delete", "head", "options", Route = "{**path}")] HttpRequest request,
        CancellationToken cancellationToken) =>
        webApp.DispatchAsync(request.HttpContext, cancellationToken);
}
