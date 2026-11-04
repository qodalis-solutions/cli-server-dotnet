using Microsoft.AspNetCore.Http;

namespace Qodalis.Cli.Plugin.Users.Models;

/// <summary>
/// Adapter interface that bridges host-application authentication (e.g. ASP.NET Identity,
/// JWT Bearer) into the CLI users plugin model. When registered, the plugin skips its own
/// token-based auth and delegates to this adapter instead.
/// <para>
/// Implement <see cref="AuthenticateAsync"/> to load the user, their roles or their disabled state
/// from a database; implement <see cref="Authenticate"/> when the request alone is enough. The plugin
/// always calls <see cref="AuthenticateAsync"/>, whose default implementation calls
/// <see cref="Authenticate"/>.
/// </para>
/// </summary>
public interface IExternalAuthAdapter
{
    /// <summary>
    /// Inspects the incoming <see cref="HttpContext"/> and returns an
    /// <see cref="ExternalAuthResult"/> if the request is authenticated,
    /// or <c>null</c> if it is not. Adapters that implement <see cref="AuthenticateAsync"/>
    /// need not implement this method.
    /// </summary>
    ExternalAuthResult? Authenticate(HttpContext context) =>
        throw new NotSupportedException(
            $"{GetType().Name} must implement {nameof(Authenticate)} or {nameof(AuthenticateAsync)}.");

    /// <summary>
    /// Asynchronous form of <see cref="Authenticate"/>, called for every authenticated request and
    /// socket upgrade, and to re-check long-lived sockets on a rebuilt request (see
    /// <see cref="Services.ExternalRequestSnapshot"/>). The default implementation calls
    /// <see cref="Authenticate"/>.
    /// </summary>
    ValueTask<ExternalAuthResult?> AuthenticateAsync(HttpContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Authenticate(context));
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

    /// <summary>
    /// Permissions granted directly by the host application (e.g. <c>logs:read</c>, <c>commands:execute</c>),
    /// added to those of the user's groups (and of <c>admin</c> with <see cref="IsAdmin"/>). Lets hosts with
    /// their own roles grant permissions without a group per permission in the group store; group names
    /// missing from the group store grant nothing.
    /// </summary>
    public IReadOnlyCollection<string>? Permissions { get; set; }
}

/// <summary>
/// Convenience <see cref="IExternalAuthAdapter"/> implementation backed by a delegate
/// (synchronous or asynchronous).
/// </summary>
public class DelegateAuthAdapter : IExternalAuthAdapter
{
    private readonly Func<HttpContext, CancellationToken, ValueTask<ExternalAuthResult?>> _func;

    public DelegateAuthAdapter(Func<HttpContext, ExternalAuthResult?> func)
    {
        ArgumentNullException.ThrowIfNull(func);
        _func = (context, _) => ValueTask.FromResult(func(context));
    }

    public DelegateAuthAdapter(Func<HttpContext, CancellationToken, ValueTask<ExternalAuthResult?>> func)
    {
        ArgumentNullException.ThrowIfNull(func);
        _func = func;
    }

    public ExternalAuthResult? Authenticate(HttpContext context) =>
        _func(context, context.RequestAborted).AsTask().GetAwaiter().GetResult();

    public ValueTask<ExternalAuthResult?> AuthenticateAsync(HttpContext context, CancellationToken cancellationToken) =>
        _func(context, cancellationToken);
}
