using Amazon.IdentityManagement;
using Amazon.IdentityManagement.Model;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Plugin.Aws.Services;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Plugin.Aws.Processors;

/// <summary>
/// Handles the "iam users" command to list all IAM users in the account.
/// </summary>
internal class IamUsersProcessor : CliCommandProcessor, ICliCommandProcessor
{
    /// <inheritdoc />
    public override string Command { get; set; } = "users";

    /// <inheritdoc />
    public override string Description { get; set; } = "List IAM users";

    /// <inheritdoc />
    public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } =
    [
        new CliCommandParameterDescriptor
        {
            Name = "region",
            Aliases = ["-r"],
            Description = "AWS region override",
            Type = CommandParameterType.String,
        },
    ];

    private readonly AwsCredentialManager _credentialManager;

    /// <summary>
    /// Initializes a new instance of <see cref="IamUsersProcessor"/>.
    /// </summary>
    /// <param name="credentialManager">The credential manager used to create the IAM client.</param>
    public IamUsersProcessor(AwsCredentialManager credentialManager) => _credentialManager = credentialManager;

    /// <inheritdoc />
    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    async Task<ICliStructuredResponse?> ICliCommandProcessor.HandleStructuredAsync(CliProcessCommand command, CancellationToken ct = default)
    {
        var builder = new CliResponseBuilder();
        var regionOverride = command.Args.TryGetValue("region", out var r) ? r.ToString() : null;
        var client = _credentialManager.GetClient<AmazonIdentityManagementServiceClient>(regionOverride);

        try
        {
            var response = await client.ListUsersAsync(new ListUsersRequest(), ct);
            var users = response.Users ?? [];

            if (users.Count == 0)
            {
                builder.WriteText("No IAM users found.", "warning");
                return builder.Build();
            }

            var headers = new[] { "UserName", "UserId", "Arn", "CreateDate" };
            var rows = users.Select(user => new[]
            {
                user.UserName ?? "(unknown)",
                user.UserId ?? "(unknown)",
                user.Arn ?? "(unknown)",
                user.CreateDate != default ? user.CreateDate.ToString("yyyy-MM-dd") : "(unknown)",
            }).ToArray();

            builder.WriteTable(headers, rows);
        }
        catch (Exception ex)
        {
            builder.WriteText($"Failed to list IAM users: {ex.Message}", "error");
            builder.SetExitCode(1);
        }

        return builder.Build();
    }
}

/// <summary>
/// Handles the "iam roles" command to list all IAM roles in the account.
/// </summary>
internal class IamRolesProcessor : CliCommandProcessor, ICliCommandProcessor
{
    /// <inheritdoc />
    public override string Command { get; set; } = "roles";

    /// <inheritdoc />
    public override string Description { get; set; } = "List IAM roles";

    /// <inheritdoc />
    public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } =
    [
        new CliCommandParameterDescriptor
        {
            Name = "region",
            Aliases = ["-r"],
            Description = "AWS region override",
            Type = CommandParameterType.String,
        },
    ];

    private readonly AwsCredentialManager _credentialManager;

    /// <summary>
    /// Initializes a new instance of <see cref="IamRolesProcessor"/>.
    /// </summary>
    /// <param name="credentialManager">The credential manager used to create the IAM client.</param>
    public IamRolesProcessor(AwsCredentialManager credentialManager) => _credentialManager = credentialManager;

    /// <inheritdoc />
    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    async Task<ICliStructuredResponse?> ICliCommandProcessor.HandleStructuredAsync(CliProcessCommand command, CancellationToken ct = default)
    {
        var builder = new CliResponseBuilder();
        var regionOverride = command.Args.TryGetValue("region", out var r) ? r.ToString() : null;
        var client = _credentialManager.GetClient<AmazonIdentityManagementServiceClient>(regionOverride);

        try
        {
            var response = await client.ListRolesAsync(new ListRolesRequest(), ct);
            var roles = response.Roles ?? [];

            if (roles.Count == 0)
            {
                builder.WriteText("No IAM roles found.", "warning");
                return builder.Build();
            }

            var headers = new[] { "RoleName", "RoleId", "Arn", "CreateDate" };
            var rows = roles.Select(role => new[]
            {
                role.RoleName ?? "(unknown)",
                role.RoleId ?? "(unknown)",
                role.Arn ?? "(unknown)",
                role.CreateDate != default ? role.CreateDate.ToString("yyyy-MM-dd") : "(unknown)",
            }).ToArray();

            builder.WriteTable(headers, rows);
        }
        catch (Exception ex)
        {
            builder.WriteText($"Failed to list IAM roles: {ex.Message}", "error");
            builder.SetExitCode(1);
        }

        return builder.Build();
    }
}

/// <summary>
/// Handles the "iam policies" command to list customer-managed IAM policies.
/// </summary>
internal class IamPoliciesProcessor : CliCommandProcessor, ICliCommandProcessor
{
    /// <inheritdoc />
    public override string Command { get; set; } = "policies";

    /// <inheritdoc />
    public override string Description { get; set; } = "List IAM policies (local/customer-managed)";

    /// <inheritdoc />
    public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } =
    [
        new CliCommandParameterDescriptor
        {
            Name = "region",
            Aliases = ["-r"],
            Description = "AWS region override",
            Type = CommandParameterType.String,
        },
    ];

    private readonly AwsCredentialManager _credentialManager;

    /// <summary>
    /// Initializes a new instance of <see cref="IamPoliciesProcessor"/>.
    /// </summary>
    /// <param name="credentialManager">The credential manager used to create the IAM client.</param>
    public IamPoliciesProcessor(AwsCredentialManager credentialManager) => _credentialManager = credentialManager;

    /// <inheritdoc />
    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    async Task<ICliStructuredResponse?> ICliCommandProcessor.HandleStructuredAsync(CliProcessCommand command, CancellationToken ct = default)
    {
        var builder = new CliResponseBuilder();
        var regionOverride = command.Args.TryGetValue("region", out var r) ? r.ToString() : null;
        var client = _credentialManager.GetClient<AmazonIdentityManagementServiceClient>(regionOverride);

        try
        {
            var response = await client.ListPoliciesAsync(new ListPoliciesRequest
            {
                Scope = PolicyScopeType.Local,
            }, ct);

            var policies = response.Policies ?? [];

            if (policies.Count == 0)
            {
                builder.WriteText("No IAM policies found.", "warning");
                return builder.Build();
            }

            var headers = new[] { "PolicyName", "Arn", "AttachmentCount" };
            var rows = policies.Select(policy => new[]
            {
                policy.PolicyName ?? "(unknown)",
                policy.Arn ?? "(unknown)",
                policy.AttachmentCount.ToString(),
            }).ToArray();

            builder.WriteTable(headers, rows);
        }
        catch (Exception ex)
        {
            builder.WriteText($"Failed to list IAM policies: {ex.Message}", "error");
            builder.SetExitCode(1);
        }

        return builder.Build();
    }
}

/// <summary>
/// Parent processor for IAM commands, aggregating users, roles, and policies sub-commands.
/// </summary>
public class IamProcessor : CliCommandProcessor, ICliCommandProcessor
{
    /// <inheritdoc />
    public override string Command { get; set; } = "iam";

    /// <inheritdoc />
    public override string Description { get; set; } = "AWS IAM operations — users, roles, policies";

    /// <inheritdoc />
    public override IEnumerable<ICliCommandProcessor>? Processors { get; set; }

    /// <summary>
    /// Initializes a new instance of <see cref="IamProcessor"/> with its sub-command processors.
    /// </summary>
    /// <param name="credentialManager">The credential manager passed to child processors.</param>
    public IamProcessor(AwsCredentialManager credentialManager)
    {
        Processors =
        [
            new IamUsersProcessor(credentialManager),
            new IamRolesProcessor(credentialManager),
            new IamPoliciesProcessor(credentialManager),
        ];
    }

    /// <inheritdoc />
    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);
}
