using Microsoft.AspNetCore.Builder;

namespace FunctionsWebAppHost.Internal;

internal sealed record WebAppRegistration(
    Action<WebApplicationBuilder> ConfigureBuilder,
    Action<WebApplication> ConfigureApplication);
