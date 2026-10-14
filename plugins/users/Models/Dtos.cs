using System.Text.Json.Serialization;

namespace Qodalis.Cli.Plugin.Users.Models;

/// <summary>
/// DTO for creating a new user (<c>POST /users</c>).
/// </summary>
public class CreateUserDto
{
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? Password { get; set; }
    public List<string>? Groups { get; set; }
    public bool? Disabled { get; set; }
    public string? HomeDir { get; set; }
    public Dictionary<string, object>? Data { get; set; }
}

/// <summary>
/// DTO for updating an existing user (<c>PUT /users/{idOrName}</c>).
/// Null fields are left unchanged.
/// </summary>
public class UpdateUserDto
{
    public string? Name { get; set; }
    public string? Email { get; set; }
    public List<string>? Groups { get; set; }
    public bool? Disabled { get; set; }
    public string? HomeDir { get; set; }
    public Dictionary<string, object>? Data { get; set; }

    /// <summary>
    /// Set by the plugin on successful login. Not bindable from request bodies.
    /// </summary>
    [JsonIgnore]
    public long? LastLoginAt { get; set; }
}

/// <summary>
/// Request body for login (<c>POST /sessions</c>). <c>username</c> may be a user name or an email.
/// </summary>
public class LoginRequest
{
    public string? Username { get; set; }
    public string? Password { get; set; }
}

/// <summary>
/// Response body for login: <c>{ token, session }</c>.
/// </summary>
public class LoginResponse
{
    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    [JsonPropertyName("session")]
    public CurrentSession Session { get; set; } = new();
}

/// <summary>
/// Request body for <c>POST /users/{idOrName}/verify-password</c>.
/// </summary>
public class VerifyPasswordRequest
{
    public string? Password { get; set; }
}

/// <summary>
/// Response body for password verification.
/// </summary>
public class VerifyResponse
{
    [JsonPropertyName("valid")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public bool Valid { get; set; }
}

/// <summary>
/// Request body for <c>PUT /users/{idOrName}/password</c>.
/// </summary>
public class PasswordChangeRequest
{
    public string? Password { get; set; }
    public string? CurrentPassword { get; set; }
}

/// <summary>
/// Response body for <c>GET /users/{idOrName}/permissions</c>.
/// </summary>
public class UserPermissionsResponse
{
    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    [JsonPropertyName("groups")]
    public List<string> Groups { get; set; } = [];

    [JsonPropertyName("permissions")]
    public List<string> Permissions { get; set; } = [];
}

/// <summary>
/// Options for querying audit entries.
/// </summary>
public class AuditQueryOptions
{
    public string? Event { get; set; }
    public string? Username { get; set; }
    public int? Limit { get; set; }
}

/// <summary>
/// Request body for creating a group (<c>POST /groups</c>).
/// </summary>
public class CreateGroupRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<string>? Permissions { get; set; }
}

/// <summary>
/// Request body for updating a group (<c>PUT /groups/{name}</c>). Null fields are left unchanged.
/// </summary>
public class UpdateGroupDto
{
    public string? Description { get; set; }
    public List<string>? Permissions { get; set; }
}

/// <summary>
/// Error body returned by every failing users endpoint: <c>{ "error": "..." }</c>.
/// </summary>
public class ErrorResponse
{
    public ErrorResponse(string error) => Error = error;

    [JsonPropertyName("error")]
    public string Error { get; set; }
}
