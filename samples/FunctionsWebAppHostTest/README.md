# Controllers and minimal APIs sample

This .NET 10 Azure Functions app demonstrates ASP.NET Core controllers,
minimal APIs, middleware and request-scoped services using
[FunctionsWebAppHost](../../README.md).

[Program.cs](./Program.cs) configures application DI and the HTTP pipeline using
the two `HostWebApp` callbacks. [WebAppFunction.cs](./WebAppFunction.cs) is the
consumer-owned catch-all trigger and depends only on public `IWebAppHost`.
There is no consumer-side server implementation or manual inner-host lifecycle.

## Run locally

Requires the .NET 10 SDK/runtime and Azure Functions Core Tools v4.
From the repository root:

```bash
cd samples/FunctionsWebAppHostTest
cp local.settings.example.json local.settings.json
func host start --port 7101
```

The [example settings](./local.settings.example.json) contain no credentials;
`local.settings.json` is ignored by Git. No storage
emulator is needed for these HTTP-only probes. The empty route prefix in
[host.json](./host.json) allows `/controller/...` and `/minimal/...` directly.
Stop the host with Ctrl+C after verification.

Use [requests.http](./requests.http) for manual calls. The sample's services,
scope header, exception-to-status mapping, endpoint mappings and upload logging
are app-specific choices, not library defaults.

## HTTP checks

With the sample running, execute the included verification script from the
repository root. It requires Python 3 and uses only its standard library:

```bash
python3 samples/FunctionsWebAppHostTest/verify_http.py \
  --base-url http://localhost:7101 --upload-bytes 65536
```

It checks controller/minimal bindings, 404/405, malformed JSON handling,
headers/cookies, binary output, Problem Details, 20 distinct request scopes,
timed response streaming and exact upload echo content. Compare its producer
timestamps with `UploadProbe` server logs to check that large uploads are read
before EOF. Response chunks must arrive separately with at least 0.5-second gaps.

## Transport and security considerations

The streaming endpoint emits three chunks one second apart. Upload chunk
delivery and response timing depend on the Functions transport; small uploads
may be coalesced. Do not rely on receiving a response before upload completion.

These anonymous endpoints are for local diagnostics, not a production
authentication policy. Configure authentication and authorization for your
deployment. Azure-hosted behavior, including Flex Consumption, has not been
validated. Swagger/OpenAPI is not configured by this sample.

See the [library responsibilities and limitations](../../README.md) before
using the adapter in another app.
