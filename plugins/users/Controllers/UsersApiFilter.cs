using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Plugin.Users.Extensions;
using Qodalis.Cli.Plugin.Users.Models;
using Qodalis.Cli.Plugin.Users.Services;

namespace Qodalis.Cli.Plugin.Users.Controllers;

/// <summary>Marks an action as reachable without authentication (login).</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class UsersAnonymousAttribute : Attribute;

/// <summary>Marks an action as answering 501 when an external auth adapter is configured.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class UsersCredentialEndpointAttribute : Attribute;

/// <summary>Marks an action as allowed while the user must change their password.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class UsersAllowPasswordChangeRequiredAttribute : Attribute;

/// <summary>Requires a permission for an action (403 when missing).</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class UsersPermissionAttribute(string permission) : Attribute
{
    public string Permission { get; } = permission;
}

/// <summary>JSON settings for the plugin's own responses, independent of the host's settings.</summary>
internal static class UsersJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static JsonResult Result(int status, object? value) =>
        new(value, Options) { StatusCode = status };

    public static JsonResult Error(int status, string message) => Result(status, new ErrorResponse(message));
}

/// <summary>
/// Applied to every users-plugin controller. In order: 501 for credential endpoints in external
/// mode; 401 when the users middleware did not authenticate the request (fail closed);
/// 403 for missing permissions; 400 <c>{error}</c> for model-binding failures; and maps
/// <see cref="UsersApiException"/> to <c>{error}</c> responses.
/// Runs before the framework's model-state / content-type filters.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class UsersApiFilterAttribute : Attribute, IAsyncActionFilter, IOrderedFilter
{
    public const string ExternalAuthMessage = "Authentication is managed by the host application";

    public int Order => int.MinValue + 1000;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var action = context.ActionDescriptor as ControllerActionDescriptor;
        var method = action?.MethodInfo;
        var options = context.HttpContext.RequestServices.GetService<UsersModuleOptions>();

        if (method?.IsDefined(typeof(UsersCredentialEndpointAttribute), false) == true && options?.UsesExternalAuth == true)
        {
            context.Result = UsersJson.Error(501, ExternalAuthMessage);
            return;
        }

        if (method?.IsDefined(typeof(UsersAnonymousAttribute), false) != true)
        {
            var auth = context.HttpContext.GetUsersAuth();
            if (auth == null)
            {
                context.Result = UsersJson.Error(401, "unauthorized");
                return;
            }

            if (auth.User.MustChangePassword &&
                method?.IsDefined(typeof(UsersAllowPasswordChangeRequiredAttribute), false) != true)
            {
                context.Result = UsersJson.Error(403, AuthService.PasswordChangeRequiredMessage);
                return;
            }

            foreach (var required in method?.GetCustomAttributes(typeof(UsersPermissionAttribute), false)
                         .Cast<UsersPermissionAttribute>() ?? [])
            {
                if (!auth.Can(required.Permission))
                {
                    context.Result = UsersJson.Error(403, $"permission denied: {required.Permission}");
                    return;
                }
            }
        }

        if (!context.ModelState.IsValid)
        {
            var message = context.ModelState
                .Where(kvp => kvp.Value?.Errors.Count > 0)
                .Select(kvp =>
                {
                    var error = kvp.Value!.Errors[0];
                    var text = !string.IsNullOrEmpty(error.ErrorMessage) ? error.ErrorMessage : error.Exception?.Message;
                    return string.IsNullOrEmpty(text) ? null : text;
                })
                .FirstOrDefault(m => m != null) ?? "invalid request";
            context.Result = UsersJson.Error(400, message);
            return;
        }

        var executed = await next();
        if (executed.Exception is UsersApiException ex && !executed.ExceptionHandled)
        {
            executed.Result = UsersJson.Error(ex.StatusCode, ex.Message);
            executed.ExceptionHandled = true;
        }
    }
}

/// <summary>Base class for the plugin's controllers.</summary>
[UsersApiFilter]
public abstract class UsersControllerBase : ControllerBase
{
    /// <summary>The authenticated principal; guaranteed non-null in non-anonymous actions.</summary>
    protected UsersAuthContext Auth => HttpContext.GetUsersAuth()
        ?? throw UsersApiException.Unauthorized();

    protected static JsonResult Json(int status, object? value) => UsersJson.Result(status, value);

    protected static JsonResult JsonOk(object? value) => UsersJson.Result(200, value);

    protected static UsersApiException Denied(string permission) =>
        UsersApiException.Forbidden($"permission denied: {permission}");
}
