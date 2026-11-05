namespace Qodalis.Cli.Plugin.Users.Models;

/// <summary>
/// Audit event names logged by the plugin.
/// </summary>
public static class AuditEvents
{
    public const string Login = "login";
    public const string LoginFailed = "login_failed";
    public const string Logout = "logout";
    public const string UserCreated = "user_created";
    public const string UserUpdated = "user_updated";
    public const string UserDeleted = "user_deleted";
    public const string UserDisabled = "user_disabled";
    public const string UserEnabled = "user_enabled";
    public const string PasswordChanged = "password_changed";
    public const string GroupCreated = "group_created";
    public const string GroupUpdated = "group_updated";
    public const string GroupDeleted = "group_deleted";
    public const string GroupMemberAdded = "group_member_added";
    public const string GroupMemberRemoved = "group_member_removed";
    public const string SessionRevoked = "session_revoked";
    public const string AuditCleared = "audit_cleared";
    public const string ShellOpened = "shell_opened";
    public const string ShellClosed = "shell_closed";
    public const string TokenCreated = "token_created";
    public const string TokenRevoked = "token_revoked";
}
