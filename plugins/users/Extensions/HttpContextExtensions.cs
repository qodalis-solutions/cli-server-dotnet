using Microsoft.AspNetCore.Http;
using Qodalis.Cli.Plugin.Users.Models;

namespace Qodalis.Cli.Plugin.Users.Extensions;

/// <summary>
/// Extension methods for accessing the users plugin session from HttpContext.
/// </summary>
public static class HttpContextExtensions
{
    /// <summary>
    /// Gets the users plugin session from HttpContext.Items, or null if not authenticated.
    /// </summary>
    public static Session? GetUsersSession(this HttpContext context) =>
        context.Items["UsersSession"] as Session;

    /// <summary>
    /// Returns true if the current user belongs to the "admin" group.
    /// </summary>
    public static bool IsUsersAdmin(this HttpContext context) =>
        context.GetUsersSession()?.User.Groups.Contains("admin") == true;
}
