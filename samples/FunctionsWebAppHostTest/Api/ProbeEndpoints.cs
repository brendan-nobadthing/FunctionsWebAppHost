using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FunctionsWebAppHostTest.Api;

public static class ProbeEndpoints
{
    public static void MapProbeEndpoints(this WebApplication app)
    {
        app.MapGet("/", () => TypedResults.Text("FunctionsWebAppHost consumer"));
        app.MapGet("/minimal/{id:int}", (int id, string? message, RequestIdentity identity,
            HttpContext context, CancellationToken cancellationToken) =>
        {
            if (!ReferenceEquals(identity, context.RequestServices.GetRequiredService<RequestIdentity>()))
            {
                throw new InvalidOperationException("Application request scope is inconsistent.");
            }
            return TypedResults.Ok(new ProbeResponse(id, message ?? "", identity.Id));
        });
        app.MapPost("/minimal/echo", (EchoRequest request, CancellationToken cancellationToken) => TypedResults.Ok(request));
        app.MapGet("/response", (HttpContext context, CancellationToken cancellationToken) =>
        {
            context.Response.Headers["X-Adapter-Probe"] = "passed";
            context.Response.Cookies.Append("adapter", "one");
            context.Response.Cookies.Append("second", "two");
            return TypedResults.Json(new EchoRequest("accepted"), statusCode: StatusCodes.Status202Accepted);
        });
        app.MapGet("/binary", (CancellationToken cancellationToken) =>
            TypedResults.Bytes(new byte[] { 0, 1, 127, 128, 255 }, "application/octet-stream"));
        app.MapGet("/stream", StreamResponseAsync);
        app.MapPost("/stream/echo", StreamEchoAsync);
        app.MapGet("/error", (RequestDelegate)(_ => throw new InvalidOperationException("Intentional adapter probe failure.")));
    }

    private static async Task StreamResponseAsync(HttpContext context, CancellationToken cancellationToken)
    {
        context.Response.ContentType = "text/plain";
        for (var index = 1; index <= 3; index++)
        {
            await context.Response.WriteAsync($"chunk-{index}\n", cancellationToken);
            await context.Response.Body.FlushAsync(cancellationToken);
            if (index < 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
    }

    private static async Task StreamEchoAsync(
        HttpContext context, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("UploadProbe");
        context.Response.ContentType = "application/octet-stream";
        var buffer = new byte[4096];
        int count;
        while ((count = await context.Request.Body.ReadAsync(buffer, cancellationToken)) > 0)
        {
            logger.LogInformation("Consumed {Count} upload bytes at {Time}; request EOF not yet observed",
                count, DateTimeOffset.UtcNow);
            await context.Response.BodyWriter.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            await context.Response.BodyWriter.FlushAsync(cancellationToken);
        }
        logger.LogInformation("Upload EOF observed at {Time}", DateTimeOffset.UtcNow);
    }
}
