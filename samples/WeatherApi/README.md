# Weather API sample

A minimal API using [FunctionsWebAppHost](../../README.md), based on the
familiar ASP.NET Core weather forecast example. It has one endpoint,
`GET /weatherforecast`, returning five days of randomly generated forecasts.
There are no controllers or external services.

[Program.cs](./Program.cs) configures the web app, its endpoint and documentation.
[WebAppFunction.cs](./WebAppFunction.cs) forwards GET requests into that app.

## Run locally

Requires the .NET 10 SDK and Azure Functions Core Tools v4.
From the repository root:

```bash
cd samples/WeatherApi
cp local.settings.example.json local.settings.json
func host start --port 7102
```

- Forecast: <http://localhost:7102/weatherforecast>
- OpenAPI JSON: <http://localhost:7102/openapi/v1.json>
- Scalar UI: <http://localhost:7102/scalar/v1>

Use [requests.http](./requests.http) for manual calls.

## OpenAPI and Scalar

`Microsoft.AspNetCore.OpenApi` generates the document from the minimal API's
endpoint metadata. `Scalar.AspNetCore` provides an interactive UI for that
document. Neither uses Functions-specific OpenAPI annotations.

Register `AddOpenApi()` on `webBuilder.Services`, not the worker's
`builder.Services`: the web app has its own DI container.
`MapOpenApi()` and `MapScalarApiReference()` expose documentation in Development
or when `EnableApiDocumentation` is true. The example settings select
Development locally; the Azure demo uses the explicit flag and stays in Production.

## Deploy to Azure

See [sample deployment instructions](../infra/README.md) to configure,
provision, deploy and remove this sample independently.

The endpoint returns demonstration data, not real weather information.
Anonymous access is for this local sample; choose an appropriate authentication
policy for a production application.
