using Microsoft.AspNetCore.Http;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Plugin.Users.Services;

namespace Qodalis.Cli.Plugin.Users.Extensions;

/// <summary>
/// Extension methods for accessing the users plugin principal from HttpContext.
/// </summary>
public static class HttpContextExtensions
{
    /// <summary>
    /// Gets the principal authenticated by <c>UseQodalisUsers()</c>, or null when the request
    /// was not authenticated by the users middleware.
    /// </summary>
    public static UsersAuthContext? GetUsersAuth(this HttpContext context) =>
        context.Items.TryGetValue(UsersAuthContext.ItemKey, out var value) ? value as UsersAuthContext : null;

    internal static void SetUsersAuth(this HttpContext context, UsersAuthContext auth) =>
        context.Items[UsersAuthContext.ItemKey] = auth;

    /// <summary>Gets the authenticated live user, or null.</summary>
    public static User? GetUsersUser(this HttpContext context) => context.GetUsersAuth()?.User;

    /// <summary>True when the authenticated user has <paramref name="permission"/>.</summary>
    public static bool HasUsersPermission(this HttpContext context, string permission) =>
        context.GetUsersAuth()?.Can(permission) == true;

    /// <summary>Returns true if the current user belongs to the <c>admin</c> group (case-insensitive).</summary>
    public static bool IsUsersAdmin(this HttpContext context) =>
        context.GetUsersAuth()?.User.IsInGroup(Group.AdminGroup) == true;
}
