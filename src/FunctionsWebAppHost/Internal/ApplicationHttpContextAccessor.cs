using Microsoft.AspNetCore.Http;

namespace FunctionsWebAppHost.Internal;

internal sealed class ApplicationHttpContextAccessor : IHttpContextAccessor
{
    // Unlike the standard accessor's static AsyncLocal, this isolates the inner host.
    private readonly AsyncLocal<ContextHolder?> _current = new();

    public HttpContext? HttpContext
    {
        get => _current.Value?.Context;
        set
        {
            if (_current.Value is { } holder)
            {
                holder.Context = null;
            }

            _current.Value = value is null ? null : new ContextHolder { Context = value };
        }
    }

    private sealed class ContextHolder
    {
        public HttpContext? Context;
    }
}
