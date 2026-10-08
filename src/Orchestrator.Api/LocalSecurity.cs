using Microsoft.Extensions.Options;
using Orchestrator.Core;

namespace Orchestrator.Api;

/// <summary>
/// The server runs commands and edits agent config files on the user's behalf, so a web page must not be able to
/// drive it from the browser (CSRF) or reach it under another name (DNS rebinding):
/// <list type="bullet">
/// <item>Host must be a loopback name, the PublicUrl host or a configured trusted host (else 400).</item>
/// <item>An Origin header, when present, must name one of those hosts (else 403). Browsers always send it cross-site.</item>
/// <item>State-changing <c>/api</c> calls must carry <see cref="ClientHeader"/>. A custom header forces a CORS preflight,
/// which this server never approves, so a page on another site cannot send it.</item>
/// </list>
/// </summary>
public static class LocalSecurity
{
    public const string ClientHeader = "X-Orchestrator-Client";

    public static IApplicationBuilder UseLocalSecurity(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var options = context.RequestServices.GetRequiredService<IOptionsMonitor<OrchestratorOptions>>().CurrentValue;
        var request = context.Request;

        if (!IsTrustedHost(request.Host.Host, options))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = $"Host '{request.Host.Host}' is not allowed. Use http://127.0.0.1:{request.Host.Port}/ or add it to Orchestrator:TrustedHosts." });
            return;
        }

        var origin = request.Headers.Origin.FirstOrDefault();
        if (!string.IsNullOrEmpty(origin)
            && !(Uri.TryCreate(origin, UriKind.Absolute, out var originUri) && IsTrustedHost(originUri.Host, options)))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = $"Cross-origin requests from '{origin}' are not allowed." });
            return;
        }

        if (request.Path.StartsWithSegments("/api")
            && !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method)
            && !request.Headers.ContainsKey(ClientHeader))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = $"Missing {ClientHeader} header. Use the dashboard, 'orch', or send '{ClientHeader}: <your tool name>'." });
            return;
        }

        await next();
    });

    public static bool IsTrustedHost(string host, OrchestratorOptions options)
    {
        host = host.Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host is "127.0.0.1" or "::1") return true;
        if (options.PublicUrl is { } publicUrl && Uri.TryCreate(publicUrl, UriKind.Absolute, out var pu)
            && pu.Host.Trim('[', ']').Equals(host, StringComparison.OrdinalIgnoreCase)) return true;
        return options.TrustedHosts.Any(h => h.Equals(host, StringComparison.OrdinalIgnoreCase));
    }
}
