using System.Security.Cryptography;
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
        app.MapPost("/stream/upload", StreamUploadAsync);
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

    private static async Task StreamUploadAsync(
        HttpContext context, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("UploadProbe");
        logger.LogInformation(
            "Upload endpoint entered at {Time:O}; before first body read; trace {TraceId}",
            DateTimeOffset.UtcNow, context.TraceIdentifier);
        long bytesReceived;
        string sha256;
        var path = Path.Combine(Path.GetTempPath(), $"fwh-upload-{Guid.NewGuid():N}.tmp");
        await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.DeleteOnClose))
        {
            var buffer = new byte[4096];
            int count;
            while ((count = await context.Request.Body.ReadAsync(buffer, cancellationToken)) > 0)
            {
                logger.LogInformation("Consumed {Count} upload bytes at {Time:O}; request EOF not yet observed; trace {TraceId}",
                    count, DateTimeOffset.UtcNow, context.TraceIdentifier);
                await file.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            }
            logger.LogInformation("Upload EOF observed at {Time:O}; trace {TraceId}",
                DateTimeOffset.UtcNow, context.TraceIdentifier);
            bytesReceived = file.Length;
            file.Position = 0;
            sha256 = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken)).ToLowerInvariant();
        }
        logger.LogInformation("Upload temporary file deleted at {Time:O}; trace {TraceId}",
            DateTimeOffset.UtcNow, context.TraceIdentifier);
        await context.Response.WriteAsJsonAsync(new { bytesReceived, sha256 }, cancellationToken);
    }
}
