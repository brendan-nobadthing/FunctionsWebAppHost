# FunctionsWebAppHost

A .NET 10 library hosting an ASP.NET Core `WebApplication` inside an Azure
Functions isolated worker. Controllers, minimal APIs and middleware run in the
same process, through a custom in-process `IServer`, not a loopback HTTP server.

This is a custom adapter, not an Azure Functions controller-routing feature.
The official HTTP integration still owns the worker's HTTP ingress server.

## Usage

Reference [FunctionsWebAppHost.csproj](./src/FunctionsWebAppHost/FunctionsWebAppHost.csproj)
from your Function app, or install the NuGet package after its first release.
The library targets `net10.0`, references `Microsoft.AspNetCore.App`, and depends
on the isolated Worker and HTTP ASP.NET Core integration packages.
The consuming executable needs Functions build tooling;
the [sample app](./samples/FunctionsWebAppHostTest/) uses
`Azure.Functions.Sdk/1.0.0`.

```csharp
using FunctionsWebAppHost;
using Microsoft.AspNetCore.Builder;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

builder.HostWebApp(
    webBuilder =>
    {
        webBuilder.Services.AddControllers();
        webBuilder.Services.AddProblemDetails();
        // Register application services, logging and serialization here.
    },
    app =>
    {
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.MapControllers();
        app.MapGet("/hello", () => "Hello from the hosted web application");
    });

builder.Build().Run();
```

Provide one consumer-owned trigger:

```csharp
using FunctionsWebAppHost;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;

public sealed class WebAppFunction(IWebAppHost webApp)
{
    [Function("WebApp")]
    public Task Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", "put",
            "patch", "delete", "head", "options", Route = "{**path}")]
        HttpRequest request,
        CancellationToken cancellationToken) =>
        webApp.DispatchAsync(request.HttpContext, cancellationToken);
}
```

The sample sets `extensions.http.routePrefix` to `""` in
[host.json](./samples/FunctionsWebAppHostTest/host.json). Route prefix,
allowed methods and authorization level are application choices. Anonymous
access is a local demonstration, not a production authentication policy.
The library does not generate triggers or function metadata.

`HostWebApp` returns the same Functions builder and configures the official
`ConfigureFunctionsWebApplication` integration automatically. A preceding
explicit integration call is compatible; it is unnecessary in the minimal setup.
Both callbacks are required. Repeated `HostWebApp` registration is rejected:
one hosted application per worker is supported.

## Startup responsibilities

| Always installed/owned by the library | Configurable per application |
|---|---|
| Official worker HTTP integration | Trigger route, methods, auth level and host route prefix |
| Inner builder with worker environment, content root and application name | Configuration overrides and app logging |
| Worker configuration inherited before customization | Domain DI, MVC services, serialization and application parts |
| Inner in-process `IServer` and isolated `IHttpContextAccessor` | Problem Details, exception mapping and status pages |
| One dispatcher/host instance and Build/start/stop/disposal | Authentication, authorization, CORS and middleware order |
| Fresh request features/scopes, cancellation and response forwarding | Controller/minimal routes and optional Swagger/OpenAPI |

The builder callback runs during host startup, before the inner `Build()`.
The pipeline callback runs after `Build()` and before `StartAsync()`. Each runs
once; the host cannot be restarted. Do not call `Build`, `Run`, `Start` or `Stop`
inside either callback. Register an application `IHostedService` for asynchronous
initialization instead of using an async-void callback.

The required server and context accessor are installed **after** the builder
callback so application customization cannot replace the adapter accidentally.
Controller discovery defaults to the consuming worker assembly. For controllers
in another assembly, use `AddControllers().AddApplicationPart(...)`.
MVC and application error policies must be registered by the application;
a minimal-only app can omit MVC entirely.

Startup/configuration failures are logged and rethrown. Built application
resources are disposed on failure; cleanup failures are logged without replacing
the original startup error. Shutdown rejects new requests and drains active
requests within the supplied shutdown cancellation token.

## Hosting and DI boundary

```text
Functions worker (official ingress server and worker services)
  -> HttpTrigger -> IWebAppHost.DispatchAsync
  -> inner WebApplication (in-process server and application services)
```

The hosts have separate service providers in the same .NET runtime. The library
does not replace worker `IServer`, copy worker registrations, or fall back to
worker DI. Request scopes and ambient application contexts are isolated from
the worker. Outer Functions endpoints/route values are not inherited.

Reusing a service-registration helper in both hosts creates separate singleton
instances. Explicitly sharing an externally owned singleton instance is possible;
its creator remains responsible for disposal. Worker user secrets/configuration
are inherited through `IConfiguration`, not through sharing the provider.

## HTTP behavior and limits

The adapter maps the worker-exposed method, scheme, protocol, host, path/base,
query, headers, cookies, identity, trace and connection metadata. It borrows body
streams without disposing them, creates an application DI scope per request and
links invocation/request cancellation. It forwards response status, headers,
cookies, asynchronous stream/pipe writes, completion callbacks and send-file
operations. Synchronous response writes/flushes are unsupported.

Metadata reflects what the worker exposes; client IP/scheme/identity must not be
assumed to be independently verified original-client information.
WebSockets, HTTP upgrades, trailers and TLS-specific features are not adapted.
This is not a complete replacement for every Kestrel capability.

Response streaming and incremental request reads depend on the Functions
transport. Small upload chunks may be coalesced; applications should not rely
on receiving a response before the upload completes. The in-process adapter
does not remove these transport constraints.

Azure-hosted behavior, including Flex Consumption, has not been validated.
Swagger/OpenAPI configuration belongs to the consuming application.

## Build and test

From the repository root:

```bash
dotnet build FunctionsWebAppHost.slnx --nologo
dotnet test tests/FunctionsWebAppHost.Tests/FunctionsWebAppHost.Tests.csproj --nologo --verbosity minimal
```

The [public API tests](./tests/FunctionsWebAppHost.Tests/WebAppHostTests.cs)
start only the registered application hosted service, without starting the worker
gRPC connection. The [sample README](./samples/FunctionsWebAppHostTest/README.md)
documents Core Tools ingress and streaming checks.

## Repository and packages

- [Library source](./src/FunctionsWebAppHost/)
- [Controllers and minimal APIs sample](./samples/FunctionsWebAppHostTest/)
- [Tests](./tests/FunctionsWebAppHost.Tests/)
- [Contributing and NuGet publishing](./CONTRIBUTING.md)

The initial NuGet package has not yet been published. After its first release:

```bash
dotnet add package FunctionsWebAppHost --prerelease
```

Build a local package with:

```bash
dotnet pack src/FunctionsWebAppHost/FunctionsWebAppHost.csproj --configuration Release --output artifacts
```

Licensed under [Apache License 2.0](./LICENSE).
