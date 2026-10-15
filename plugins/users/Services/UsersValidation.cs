using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Qodalis.Cli.Plugin.Users.Services;

/// <summary>
/// Input validation for the users plugin (security contract B7). Violations throw 400.
/// </summary>
public static partial class UsersValidation
{
    public const int MaxNameLength = 64;
    public const int MaxEmailLength = 254;
    public const int MaxPermissionLength = 128;
    public const int MaxPermissionsPerGroup = 100;
    public const int MaxDescriptionLength = 256;
    public const int MaxDataBytes = 16 * 1024;
    public const long MaxBodyBytes = 1024 * 1024;

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._@-]*$")]
    private static partial Regex NameRegex();

    [GeneratedRegex(@"^[^\s@]+@[^\s@]+$")]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"^(\*|[a-z0-9_.-]+(:([a-z0-9_.-]+|\*))*)$", RegexOptions.IgnoreCase)]
    private static partial Regex PermissionRegex();

    public static bool HasControlChars(string? value) =>
        value != null && value.Any(c => c <= '\u001f' || c == '\u007f');

    /// <summary>Rejects control characters (incl. ANSI ESC) in a stored string.</summary>
    public static void RejectControlChars(string field, string? value)
    {
        if (HasControlChars(value))
            throw UsersApiException.BadRequest($"{field} must not contain control characters");
    }

    /// <summary>User and group names: 1–64 chars, <c>^[A-Za-z0-9][A-Za-z0-9._@-]*$</c>.</summary>
    public static string Name(string field, string? value)
    {
        var name = value?.Trim();
        if (string.IsNullOrEmpty(name)) throw UsersApiException.BadRequest($"{field} is required");
        RejectControlChars(field, name);
        if (name.Length > MaxNameLength || !NameRegex().IsMatch(name))
            throw UsersApiException.BadRequest(
                $"invalid {field}: 1-{MaxNameLength} characters, letters, digits and . _ @ - (must start with a letter or digit)");
        return name;
    }

    /// <summary>Email: at most 254 chars, <c>local@domain</c>, no whitespace or control characters.</summary>
    public static string Email(string? value)
    {
        var email = value?.Trim();
        if (string.IsNullOrEmpty(email)) throw UsersApiException.BadRequest("email is required");
        RejectControlChars("email", email);
        if (email.Length > MaxEmailLength || !EmailRegex().IsMatch(email))
            throw UsersApiException.BadRequest("invalid email");
        return email;
    }

    public static void Description(string? value)
    {
        if (value == null) return;
        RejectControlChars("description", value);
        if (value.Length > MaxDescriptionLength)
            throw UsersApiException.BadRequest($"description must be at most {MaxDescriptionLength} characters");
    }

    public static void HomeDir(string? value)
    {
        if (value == null) return;
        RejectControlChars("homeDir", value);
        if (value.Length > 1024) throw UsersApiException.BadRequest("homeDir is too long");
    }

    public static void Data(Dictionary<string, object>? data)
    {
        if (data == null) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(data).Length;
        if (bytes > MaxDataBytes)
            throw UsersApiException.BadRequest($"data must be at most {MaxDataBytes / 1024} KB");
    }

    /// <summary>Permission strings: at most 128 chars each, at most 100 per group, valid syntax.</summary>
    public static List<string>? Permissions(List<string>? permissions)
    {
        if (permissions == null) return null;
        if (permissions.Count > MaxPermissionsPerGroup)
            throw UsersApiException.BadRequest($"at most {MaxPermissionsPerGroup} permissions per group");
        foreach (var p in permissions)
        {
            if (string.IsNullOrWhiteSpace(p))
                throw UsersApiException.BadRequest("permissions must be non-empty strings");
            var trimmed = p.Trim();
            if (trimmed.Length > MaxPermissionLength || HasControlChars(trimmed) || !PermissionRegex().IsMatch(trimmed))
                throw UsersApiException.BadRequest($"invalid permission: {Sanitize(trimmed, MaxPermissionLength)}");
        }
        return PermissionMatcher.Merge([permissions]);
    }

    /// <summary>Removes control characters and truncates (for audit details and echoed values).</summary>
    public static string Sanitize(string? value, int maxLength = 512)
    {
        if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
        var sb = new StringBuilder(Math.Min(value.Length, maxLength));
        foreach (var c in value)
        {
            if (c <= '\u001f' || c == '\u007f') continue;
            sb.Append(c);
            if (sb.Length >= maxLength) break;
        }
        return sb.ToString();
    }
}
