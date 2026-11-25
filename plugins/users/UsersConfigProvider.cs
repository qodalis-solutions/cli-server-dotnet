using System.Text.Json.Nodes;
using Qodalis.Cli.Abstractions.Config;

namespace Qodalis.Cli.Plugin.Users;

/// <summary>
/// <c>users</c>: sessions, lockout and passwords, changeable at runtime (read on every sign-in, request and password
/// change); how the plugin protects the server is read-only.
/// </summary>
public static class UsersConfigProvider
{
    private const long Minute = 60_000;
    private const long Day = 86_400_000;

    public static CliStoredConfigProvider Create(UsersModuleOptions options)
    {
        var sessionTimeout = CliConfigValues.FormatDuration(options.EffectiveSessionTimeoutMs);
        var maxSessionLifetime = CliConfigValues.FormatDuration(options.EffectiveMaxSessionLifetimeMs);
        var lockout = CliConfigValues.FormatDuration(options.LockoutMs);
        var maxLoginAttempts = options.MaxLoginAttempts;
        var minPasswordLength = options.MinPasswordLength;

        static long Ms(JsonNode? value, string fallback) =>
            CliConfigValues.ParseDuration(value) ?? CliConfigValues.ParseDuration(fallback)!.Value;
        static string? Whole(JsonNode value) => CliConfigValues.IsInteger(value) ? null : "must be a whole number";
        static CliConfigSettingDefinition ReadOnly(string key, string label, string description, JsonNode value) => new()
        {
            Key = key,
            Label = label,
            Description = description,
            Type = CliConfigValues.AsBoolean(value) != null ? CliConfigEntryTypes.Boolean : CliConfigEntryTypes.String,
            Mutable = false,
            RequiresRestart = true,
            Default = value,
        };

        var signIn = options.GuardOnly ? "guard-only" : options.UsesExternalAuth ? "external" : "users";
        return new CliStoredConfigProvider(new CliStoredConfigProviderOptions
        {
            Id = "users",
            Title = "Users",
            Description = "Sign-in sessions, lockout and passwords of the users plugin",
            Order = 70,
            Sections =
            [
                new CliConfigSectionDefinition
                {
                    Id = "sessions",
                    Title = "Sessions",
                    Settings =
                    [
                        new CliConfigSettingDefinition
                        {
                            Key = "sessionTimeout", Label = "Idle timeout", Description = "A session without activity for this long ends",
                            Type = CliConfigEntryTypes.Duration, Default = sessionTimeout,
                            Validation = new CliConfigValidation { Min = Minute, Max = 30 * Day },
                            Apply = value => options.SessionTimeoutMs = Ms(value, sessionTimeout),
                        },
                        new CliConfigSettingDefinition
                        {
                            Key = "maxSessionLifetime", Label = "Maximum lifetime", Description = "A session ends this long after sign-in, whatever its activity",
                            Type = CliConfigEntryTypes.Duration, Default = maxSessionLifetime,
                            Validation = new CliConfigValidation { Min = Minute, Max = 365 * Day },
                            Apply = value => options.MaxSessionLifetimeMs = Ms(value, maxSessionLifetime),
                        },
                    ],
                },
                new CliConfigSectionDefinition
                {
                    Id = "security",
                    Title = "Lockout and passwords",
                    Settings =
                    [
                        new CliConfigSettingDefinition
                        {
                            Key = "maxLoginAttempts", Label = "Failed sign-ins before lockout", Description = "Consecutive failures that lock an account",
                            Type = CliConfigEntryTypes.Number, Default = maxLoginAttempts,
                            Validation = new CliConfigValidation { Min = 1, Max = 100 },
                            Validate = Whole,
                            Apply = value => options.MaxLoginAttempts = CliConfigValues.AsNumber(value) is { } n ? (int)n : maxLoginAttempts,
                        },
                        new CliConfigSettingDefinition
                        {
                            Key = "lockout", Label = "Lockout", Description = "How long a locked account stays locked (also the window failures are counted in)",
                            Type = CliConfigEntryTypes.Duration, Default = lockout,
                            Validation = new CliConfigValidation { Min = 1000, Max = Day },
                            Apply = value => options.LockoutMs = Ms(value, lockout),
                        },
                        new CliConfigSettingDefinition
                        {
                            Key = "minPasswordLength", Label = "Minimum password length", Description = "Applies to new passwords",
                            Type = CliConfigEntryTypes.Number, Default = minPasswordLength,
                            Validation = new CliConfigValidation { Min = 1, Max = 1024 },
                            Validate = Whole,
                            Apply = value => options.MinPasswordLength = CliConfigValues.AsNumber(value) is { } n ? (int)n : minPasswordLength,
                        },
                    ],
                },
                new CliConfigSectionDefinition
                {
                    Id = "access",
                    Title = "Access",
                    Description = "Set in code",
                    Settings =
                    [
                        ReadOnly("protectServer", "Protects the server", "Every route and socket needs a users session", options.ProtectServer),
                        ReadOnly("signIn", "Sign-in", "users: the plugin's accounts; external: the host's auth adapter; guard-only: tokens only", signIn),
                        ReadOnly("allowNonExpiringTokens", "Non-expiring tokens", "API tokens may never expire", options.AllowNonExpiringTokens),
                        ReadOnly("apiTokensWithExternalAuth", "Tokens with external sign-in", "API tokens accepted beside the host's adapter", options.ApiTokensWithExternalAuth),
                        ReadOnly("usersReadOnly", "Users managed by the host", "User writes answer 501", options.UsersReadOnly),
                        ReadOnly("groupsReadOnly", "Groups managed by the host", "Group writes answer 501", options.GroupsReadOnly),
                    ],
                },
            ],
        });
    }
}
