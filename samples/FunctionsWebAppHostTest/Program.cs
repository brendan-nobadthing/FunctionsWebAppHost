using FunctionsWebAppHost;
using FunctionsWebAppHostTest.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

builder.HostWebApp(
    webBuilder =>
    {
        webBuilder.Services.AddControllers();
        webBuilder.Services.AddProblemDetails();
        // Test-only probe for verifying per-request DI scope isolation.
        webBuilder.Services.AddScoped<RequestIdentity>();
    },
    app =>
    {
        app.UseExceptionHandler(new ExceptionHandlerOptions
        {
            StatusCodeSelector = exception => exception is BadHttpRequestException badRequest
                ? badRequest.StatusCode
                : StatusCodes.Status500InternalServerError
        });
        app.UseStatusCodePages();
        // verify_http.py compares this header with the endpoint's scopeId and checks
        // distinct IDs across concurrent requests. Normal library consumers can omit this.
        app.Use(async (context, next) =>
        {
            var identity = context.RequestServices.GetRequiredService<RequestIdentity>();
            context.Response.OnStarting(() =>
            {
                context.Response.Headers["X-Application-Scope"] = identity.Id.ToString();
                return Task.CompletedTask;
            });
            await next(context);
        });
        app.MapControllers();
        app.MapProbeEndpoints();
    });

builder.Build().Run();
