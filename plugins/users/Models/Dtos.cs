namespace Qodalis.Cli.Plugin.Users.Models;

/// <summary>
/// DTO for creating a new user.
/// </summary>
public class CreateUserDto
{
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? Password { get; set; }
    public List<string>? Groups { get; set; }
    public Dictionary<string, object>? Data { get; set; }
}

/// <summary>
/// DTO for updating an existing user.
/// </summary>
public class UpdateUserDto
{
    public string? Name { get; set; }
    public string? Email { get; set; }
    public List<string>? Groups { get; set; }
    public bool? Disabled { get; set; }
    public Dictionary<string, object>? Data { get; set; }
}

/// <summary>
/// Request body for login.
/// </summary>
public class LoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

/// <summary>
/// Response body for login.
/// </summary>
public class LoginResponse
{
    public string Token { get; set; } = string.Empty;
    public Session Session { get; set; } = new();
}

/// <summary>
/// Request body for password verification.
/// </summary>
public class VerifyRequest
{
    public string UserId { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

/// <summary>
/// Response body for password verification.
/// </summary>
public class VerifyResponse
{
    public bool Valid { get; set; }
}

/// <summary>
/// Request body for changing a password.
/// </summary>
public class PasswordChangeRequest
{
    public string? CurrentPassword { get; set; }
    public string NewPassword { get; set; } = string.Empty;
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
/// Request body for creating a group.
/// </summary>
public class CreateGroupRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
}
