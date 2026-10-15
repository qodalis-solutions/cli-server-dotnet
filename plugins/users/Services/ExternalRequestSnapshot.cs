using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace Qodalis.Cli.Plugin.Users.Services;

/// <summary>
/// The parts of a request that an <see cref="Models.IExternalAuthAdapter"/> accepted (method, URL,
/// headers including cookies, remote address). An <see cref="HttpContext"/> cannot be used after its
/// request, so long-lived connections (open shells) re-validate an adapter principal by running the
/// adapter again on a minimal context rebuilt from this snapshot. Only request data is available
/// there (no features or items of the original request): adapters must authenticate from headers
/// or cookies, directly or through the host's default ASP.NET Core authentication scheme.
/// </summary>
public sealed class ExternalRequestSnapshot
{
    private readonly Dictionary<string, StringValues> _headers;

    private ExternalRequestSnapshot(HttpRequest request)
    {
        Method = request.Method;
        Scheme = request.Scheme;
        Host = request.Host;
        PathBase = request.PathBase;
        Path = request.Path;
        // The WebSocket ticket is single-use and already consumed: never keep it.
        Query = QueryString.Create(request.Query
            .Where(q => !string.Equals(q.Key, "ticket", StringComparison.OrdinalIgnoreCase))
            .SelectMany(q => q.Value.Select(v => new KeyValuePair<string, string?>(q.Key, v))));
        _headers = request.Headers.ToDictionary(h => h.Key, h => h.Value, StringComparer.OrdinalIgnoreCase);
        RemoteIpAddress = request.HttpContext.Connection.RemoteIpAddress;
        RemotePort = request.HttpContext.Connection.RemotePort;
    }

    public string Method { get; }
    public string Scheme { get; }
    public HostString Host { get; }
    public PathString PathBase { get; }
    public PathString Path { get; }
    public QueryString Query { get; }
    public IPAddress? RemoteIpAddress { get; }
    public int RemotePort { get; }

    /// <summary>Captures <paramref name="context"/>'s request.</summary>
    public static ExternalRequestSnapshot Capture(HttpContext context) => new(context.Request);

    /// <summary>
    /// A fresh context carrying the captured request. <paramref name="services"/> becomes
    /// <see cref="HttpContext.RequestServices"/> (the original request scope may be gone).
    /// </summary>
    public HttpContext ToHttpContext(IServiceProvider? services = null)
    {
        var context = new DefaultHttpContext();
        var request = context.Request;
        request.Method = Method;
        request.Scheme = Scheme;
        request.Host = Host;
        request.PathBase = PathBase;
        request.Path = Path;
        request.QueryString = Query;
        foreach (var (name, value) in _headers)
            request.Headers[name] = value;
        context.Connection.RemoteIpAddress = RemoteIpAddress;
        context.Connection.RemotePort = RemotePort;
        if (services != null) context.RequestServices = services;
        return context;
    }

    /// <summary>
    /// <see cref="ToHttpContext"/>, then — when the host registered ASP.NET Core authentication with a
    /// default scheme — runs that scheme on the captured headers/cookies and sets
    /// <see cref="HttpContext.User"/>, as the authentication middleware did for the original request.
    /// </summary>
    public async Task<HttpContext> ToAuthenticatedHttpContextAsync(IServiceProvider? services = null)
    {
        var context = ToHttpContext(services);
        var schemes = services?.GetService<IAuthenticationSchemeProvider>();
        if (schemes == null || services!.GetService<IAuthenticationService>() == null) return context;
        if (await schemes.GetDefaultAuthenticateSchemeAsync() == null) return context;

        var result = await context.AuthenticateAsync();
        if (result.Succeeded && result.Principal != null) context.User = result.Principal;
        return context;
    }
}
