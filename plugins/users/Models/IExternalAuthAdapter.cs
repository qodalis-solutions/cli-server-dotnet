using Microsoft.AspNetCore.Http;

namespace Qodalis.Cli.Plugin.Users.Models;

/// <summary>
/// Adapter interface that bridges host-application authentication (e.g. ASP.NET Identity,
/// JWT Bearer) into the CLI users plugin model. When registered, the plugin skips its own
/// token-based auth and delegates to this adapter instead.
/// </summary>
public interface IExternalAuthAdapter
{
    /// <summary>
    /// Inspects the incoming <see cref="HttpContext"/> and returns an
    /// <see cref="ExternalAuthResult"/> if the request is authenticated,
    /// or <c>null</c> if it is not.
    /// </summary>
    ExternalAuthResult? Authenticate(HttpContext context);
}

/// <summary>
/// Result returned by an <see cref="IExternalAuthAdapter"/> on successful authentication.
/// </summary>
public class ExternalAuthResult
{
    /// <summary>
    /// The authenticated user mapped into the CLI user model.
    /// </summary>
    public User User { get; set; } = null!;

    /// <summary>
    /// Whether the authenticated user should be treated as an admin
    /// (i.e. have access to admin-only endpoints).
    /// </summary>
    public bool IsAdmin { get; set; }
}

/// <summary>
/// Convenience <see cref="IExternalAuthAdapter"/> implementation backed by a delegate.
/// </summary>
public class DelegateAuthAdapter : IExternalAuthAdapter
{
    private readonly Func<HttpContext, ExternalAuthResult?> _func;

    public DelegateAuthAdapter(Func<HttpContext, ExternalAuthResult?> func) => _func = func;

    public ExternalAuthResult? Authenticate(HttpContext context) => _func(context);
}
