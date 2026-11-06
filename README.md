# FunctionsWebAppHost

A .NET 10 library hosting an ASP.NET Core `WebApplication` inside an Azure
Functions isolated worker. Controllers, minimal APIs and middleware run in the
same process, through a custom in-process `IServer`, not a loopback HTTP server.

This is a custom adapter, not an Azure Functions controller-routing feature.
The official HTTP integration still owns the worker's HTTP ingress server.

## Why this package exists

Azure Functions provides a serverless compute platform with managed hosting
and event-driven scaling. It is an attractive place to run an HTTP API without
managing a dedicated web server, but its .NET programming model is not the same
as an ASP.NET Core web application.

With the default Functions SDK, HTTP endpoints are function methods selected
through HTTP trigger metadata. Routing, parameter binding and the invocation
pipeline follow Functions conventions rather than the controller and minimal
API conventions of ASP.NET Core. Existing web APIs and middleware therefore
cannot simply be moved into a Function app unchanged.

Microsoft's
[ASP.NET Core integration](https://learn.microsoft.com/en-us/azure/azure-functions/dotnet-isolated-process-guide#aspnet-core-integration)
narrows that gap. It provides familiar HTTP types such as `HttpRequest`,
`HttpResponse` and `IActionResult`, along with MVC serialization and request-body
binding support. However, it does not expose ASP.NET Core endpoint routing or
the application middleware pipeline. Returning an action result from a function
does not make that function an MVC controller action.

OpenAPI generation also follows a different model. Functions-specific tooling
describes function triggers and their metadata; it is not a drop-in replacement
for the endpoint descriptions and conventions used by ASP.NET Core controllers
and minimal APIs. Keeping an API in the ASP.NET Core model lets applications
use its endpoint metadata and choose compatible OpenAPI tooling, rather than
maintaining a separate Functions-specific API description. This library does
not configure or generate OpenAPI documents itself.

The project is partly inspired by
[AWS Lambda Web Adapter](https://github.com/aws/aws-lambda-web-adapter), which
lets conventional HTTP web applications run on Lambda. FunctionsWebAppHost
pursues a similar goal with a different implementation: instead of forwarding
requests to a listening web server, it supplies a custom, in-process `IServer`
that hosts an ASP.NET Core `WebApplication`. A catch-all HTTP-triggered function
dispatches requests into that application's routing and middleware pipeline.

The aim is to combine Functions hosting with the familiar ASP.NET Core
application stack: controllers, minimal APIs, model binding, dependency
injection and middleware. Functions still owns invocation and ingress, and
the adapter remains subject to the transport and feature limitations described
below. It is not a replacement for every capability of a standalone ASP.NET
Core server.

**There are two application stacks in the same process, each with its own DI
container.** Services registered for other Functions are not shared with the
web app. Register web app services separately; registering a singleton in both
stacks creates a separate instance in each.

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

## Two application stacks, separate DI

```text
Functions worker (Functions DI container)
  -> HttpTrigger -> IWebAppHost.DispatchAsync
  -> ASP.NET Core WebApplication (web app DI container)
```

The Functions worker and hosted web app run in the same process, but have
independent dependency injection containers:

- `builder.Services` configures services for Functions, including any other
  HTTP, timer or queue-triggered functions.
- `webBuilder.Services` inside `HostWebApp` configures services for the web app's
  controllers, minimal APIs and middleware.
- Registrations are not copied or shared. A service registered only for
  Functions is not available in the web app.
- A singleton is singleton **per container**, not per process. Registering the
  same singleton type in both stacks creates two instances when resolved.
  Request scopes are also separate.

The web app inherits worker configuration values through `IConfiguration`,
but this does not share its DI container or services. If you deliberately
register the same externally created instance in both containers, its creator
must manage its lifetime and disposal.

The worker's HTTP server and routes remain separate from the web app's
in-process server and ASP.NET Core routes.

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
