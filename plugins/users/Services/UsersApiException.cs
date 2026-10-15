namespace Qodalis.Cli.Plugin.Users.Services;

/// <summary>
/// Thrown by plugin services to produce an HTTP error response <c>{ "error": message }</c>
/// with the given status code.
/// </summary>
public class UsersApiException : Exception
{
    public UsersApiException(int statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; }

    public static UsersApiException BadRequest(string message) => new(400, message);
    public static UsersApiException Unauthorized(string message = "unauthorized") => new(401, message);
    public static UsersApiException Forbidden(string message) => new(403, message);
    public static UsersApiException NotFound(string message) => new(404, message);
    public static UsersApiException Conflict(string message) => new(409, message);
}
