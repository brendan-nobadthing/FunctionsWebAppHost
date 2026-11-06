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

For Azure deployment and cleanup, see the
[sample deployment instructions](../infra/README.md).

With the sample running, execute the included verification script from the
repository root. It requires Python 3 and uses only its standard library:

```bash
python3 samples/FunctionsWebAppHostTest/verify_http.py \
  --base-url http://localhost:7101
```

It checks controller/minimal bindings, 404/405, malformed JSON handling,
headers/cookies, binary output, Problem Details, 20 distinct request scopes,
timed response streaming and upload file byte count and SHA-256 checksum.
Response chunks must arrive separately with at least 0.5-second gaps.

The single upload test sends two 64 KiB chunks one second apart.
The upload endpoint streams to a temporary file, checksums it and deletes it,
including on failure or cancellation. Only after EOF and deletion does it
return a JSON byte count and SHA-256, which the client verifies.
This checks chunked upload integrity, not incremental request delivery.
In deployed .NET isolated tests, trigger methods started only after the full
request body had been received. Input streams at both function and web app
levels should therefore be treated as buffered. No duplex response is attempted.
Use `--upload-only` to skip the other checks.

## Run checks against Azure

Deploy this sample using the [deployment instructions](../infra/README.md),
then pass the HTTPS URL printed by the deployment script to the existing
`--base-url` parameter. No additional configuration is required.

From the repository root:

```powershell
$baseUrl = "https://<your-function-app-hostname>.azurewebsites.net"
Invoke-WebRequest "$baseUrl/"
python3 samples/FunctionsWebAppHostTest/verify_http.py --base-url $baseUrl
```

Use the actual hostname printed after deployment, without an `/api` suffix:
this sample has an empty route prefix. The initial request warms up the app
before the checks, which use short HTTP timeouts.

The same checks run against Azure as locally, including response streaming and
temporary-file uploads. A failed assertion or request exits with an error; investigate the output
and Application Insights logs rather than assuming the deployment passed.
Compare upload producer timestamps with the `UploadProbe` logs in Application
Insights to check when the application consumed the request body.

The sample uses anonymous HTTP triggers, so no function key is needed.

### Inspect upload timing

The sample logs function entry, upload endpoint entry before the first body
read, each completed body read, application-observed EOF and temporary-file
deletion. Each includes an
ISO 8601 UTC timestamp and the request's trace identifier. No request bodies
or query strings are logged.

After redeploying and rerunning the upload probe, use Application Insights Logs:

```kusto
traces
| where timestamp > ago(30m)
| where message startswith "Function received POST /stream/upload "
    or message startswith "Upload endpoint entered"
    or message startswith "Consumed "
    or message startswith "Upload EOF observed"
    or message startswith "Upload temporary file deleted"
| project timestamp, message, traceId = tostring(customDimensions.TraceId), operation_Id
| order by timestamp asc
```

Match the trace identifier across entries and compare with the client's
printed upload timestamps. Late function entry suggests buffering before
the trigger. Early entry followed by a late first read suggests a wait for
body data after entry. These logs do not identify which upstream component
buffered the request; allow for clock differences between client and server.
Application-observed EOF is not the same as the time the client sent EOF.

## Transport and security considerations

The streaming endpoint emits three chunks one second apart. Incremental response
delivery was verified locally and in Azure. Treat incoming request bodies as
buffered in Azure, even though local Core Tools tests read uploads incrementally.
The adapter provides ASP.NET Core pipelines, not every Kestrel capability.
Do not rely on receiving a response before upload completion.

These anonymous endpoints are for local diagnostics, not a production
authentication policy. Configure authentication and authorization for your
deployment. Swagger/OpenAPI is not configured by this sample.

See the [library responsibilities and limitations](../../README.md) before
using the adapter in another app.
