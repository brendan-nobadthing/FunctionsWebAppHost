using FunctionsWebAppHost;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace FunctionsWebAppHostTest;

public sealed class WebAppFunction(IWebAppHost webApp, ILogger<WebAppFunction> logger)
{
    [Function("WebApp")]
    public Task Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", "put", "patch",
            "delete", "head", "options", Route = "{**path}")] HttpRequest request,
        CancellationToken cancellationToken)
    {
        // In Azure, host/isolated-worker inter-process integration buffers the full request
        // before this method runs. The input stream accesses the already-buffered body.
        logger.LogInformation(
            "Function received {Method} {Path} at {Time:O}; dispatching to web app; trace {TraceId}",
            request.Method, request.Path, DateTimeOffset.UtcNow, request.HttpContext.TraceIdentifier);
        return webApp.DispatchAsync(request.HttpContext, cancellationToken);
    }
}
