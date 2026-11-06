using Microsoft.AspNetCore.Mvc;

namespace FunctionsWebAppHostTest.Api;

[ApiController]
[Route("controller")]
public sealed class ProbeController(RequestIdentity identity) : ControllerBase
{
    [HttpGet("{id:int}")]
    public ActionResult<ProbeResponse> Get(int id, [FromQuery] string? message, CancellationToken cancellationToken) =>
        Ok(new ProbeResponse(id, message ?? "", identity.Id));

    [HttpPost("echo")]
    public ActionResult<EchoRequest> Echo([FromBody] EchoRequest request, CancellationToken cancellationToken) =>
        Ok(request);
}

/// <summary>A JSON body echoed by the sample application.</summary>
public sealed record EchoRequest(string Message);

/// <summary>Demonstrates route/query binding and the application request scope.</summary>
public sealed record ProbeResponse(int Id, string Message, Guid ScopeId);

public sealed class RequestIdentity
{
    public Guid Id { get; } = Guid.NewGuid();
}
