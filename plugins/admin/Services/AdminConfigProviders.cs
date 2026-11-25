using System.Text.Json.Nodes;
using Qodalis.Cli.Abstractions.Config;

namespace Qodalis.Cli.Plugin.Admin.Services;

/// <summary>The admin plugin's config providers: <c>auth</c> (read-only) and <c>logs</c>.</summary>
public static class AdminConfigProviders
{
    /// <summary>How the dashboard signs users in: <c>password</c>, <c>users</c> or <c>external</c>.</summary>
    public static readonly IReadOnlyList<string> SignInModes = ["password", "users", "external"];

    /// <summary>
    /// <c>auth</c> (read-only): how the admin dashboard signs users in: the mode, the static admin's username, whether the
    /// default credentials or an explicit JWT secret are in use, the token lifetime and the host's sign-in URL. Never a
    /// password or the secret itself.
    /// </summary>
    public static ICliConfigProvider Auth(AdminConfig config, Func<string> mode) => new AuthProvider(config, mode);

    private sealed class AuthProvider(AdminConfig config, Func<string> mode) : ICliConfigProvider
    {
        public string Id => "auth";
        public string Title => "Authentication";
        public string? Description => "How the admin dashboard signs users in (read-only)";
        public int Order => 10;
        public CliConfigProviderStorage? Storage => new() { Kind = CliConfigStorageKinds.Options, Description = "Read from the admin plugin's options" };

        public Task<IReadOnlyList<CliConfigSection>> GetSectionsAsync(CliConfigContext context, CancellationToken cancellationToken = default)
        {
            var current = mode();
            static string SourceOf(string variable) =>
                !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(variable)) ? CliConfigSources.Environment : CliConfigSources.Default;
            IReadOnlyList<CliConfigSection> sections =
            [
                new()
                {
                    Id = "admin",
                    Title = "Admin dashboard",
                    Entries =
                    [
                        new()
                        {
                            Key = "mode", Label = "Sign-in mode",
                            Description = "password: the static admin credentials; users: the users plugin's accounts; external: the host application signs users in",
                            Type = CliConfigEntryTypes.Enum, Value = current,
                            Validation = new CliConfigValidation { Options = SignInModes.ToList() },
                        },
                        new()
                        {
                            Key = "username", Label = "Admin username", Description = "The static admin (password mode)",
                            Type = CliConfigEntryTypes.String, Value = config.Username, Source = SourceOf("QCLI_ADMIN_USERNAME"),
                        },
                        new()
                        {
                            Key = "defaultCredentials", Label = "Default credentials",
                            Description = "The well-known admin / admin credentials are in effect (password mode)",
                            Type = CliConfigEntryTypes.Boolean, Value = current == "password" && config.UsesDefaultCredentials(),
                        },
                        new()
                        {
                            Key = "jwtSecretConfigured", Label = "JWT secret set",
                            Description = "Whether JWT secret is explicitly set (otherwise random per start)",
                            Type = CliConfigEntryTypes.Boolean,
                            Value = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("QCLI_ADMIN_JWT_SECRET")),
                            Source = SourceOf("QCLI_ADMIN_JWT_SECRET"),
                        },
                        new()
                        {
                            Key = "jwtExpiry", Label = "Token lifetime", Description = "How long a dashboard sign-in lasts (password mode)",
                            Type = CliConfigEntryTypes.Duration, Value = CliConfigValues.FormatDuration(config.JwtExpiry),
                        },
                        new()
                        {
                            Key = "signInUrl", Label = "Sign-in URL", Description = "Where the host application signs users in (external mode)",
                            Type = CliConfigEntryTypes.String, Value = config.SignInUrl is { } url ? JsonValue.Create(url) : null,
                        },
                    ],
                },
            ];
            return Task.FromResult(sections);
        }
    }

    /// <summary><c>logs</c>: the admin log buffer (what the dashboard's Logs page and the logs socket show), changeable at runtime.</summary>
    public static CliStoredConfigProvider Logs(LogRingBuffer buffer)
    {
        var capacity = buffer.Capacity;
        var minLevel = buffer.MinLevel;
        return new CliStoredConfigProvider(new CliStoredConfigProviderOptions
        {
            Id = "logs",
            Title = "Logs",
            Description = "The log buffer of the admin dashboard",
            Order = 80,
            Sections =
            [
                new CliConfigSectionDefinition
                {
                    Id = "buffer",
                    Title = "Log buffer",
                    Settings =
                    [
                        new CliConfigSettingDefinition
                        {
                            Key = "capacity", Label = "Entries kept", Description = "How many log entries the server keeps in memory (the newest)",
                            Type = CliConfigEntryTypes.Number, Default = capacity,
                            Validation = new CliConfigValidation { Min = LogRingBuffer.MinCapacity, Max = LogRingBuffer.MaxCapacity },
                            Validate = value => CliConfigValues.IsInteger(value) ? null : "must be a whole number",
                            Apply = value => buffer.Resize(CliConfigValues.AsNumber(value) is { } n ? (int)n : capacity),
                        },
                        new CliConfigSettingDefinition
                        {
                            Key = "minLevel", Label = "Lowest level", Description = "Entries below this level are neither kept nor streamed",
                            Type = CliConfigEntryTypes.Enum, Default = minLevel,
                            Validation = new CliConfigValidation { Options = LogRingBuffer.Levels.ToList() },
                            Apply = value => buffer.MinLevel = CliConfigValues.AsString(value) ?? minLevel,
                        },
                    ],
                },
            ],
        });
    }
}
