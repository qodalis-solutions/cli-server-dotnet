using Amazon.IdentityManagement;
using Amazon.IdentityManagement.Model;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Plugin.Aws.Services;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Plugin.Aws.Processors;

// ---------------------------------------------------------------------------
// iam users
// ---------------------------------------------------------------------------

internal class IamUsersProcessor : CliCommandProcessor, ICliCommandProcessor
{
    public override string Command { get; set; } = "users";
    public override string Description { get; set; } = "List IAM users";

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

    public IamUsersProcessor(AwsCredentialManager credentialManager) => _credentialManager = credentialManager;

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

// ---------------------------------------------------------------------------
// iam roles
// ---------------------------------------------------------------------------

internal class IamRolesProcessor : CliCommandProcessor, ICliCommandProcessor
{
    public override string Command { get; set; } = "roles";
    public override string Description { get; set; } = "List IAM roles";

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

    public IamRolesProcessor(AwsCredentialManager credentialManager) => _credentialManager = credentialManager;

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

// ---------------------------------------------------------------------------
// iam policies
// ---------------------------------------------------------------------------

internal class IamPoliciesProcessor : CliCommandProcessor, ICliCommandProcessor
{
    public override string Command { get; set; } = "policies";
    public override string Description { get; set; } = "List IAM policies (local/customer-managed)";

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

    public IamPoliciesProcessor(AwsCredentialManager credentialManager) => _credentialManager = credentialManager;

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

// ---------------------------------------------------------------------------
// iam (parent)
// ---------------------------------------------------------------------------

public class IamProcessor : CliCommandProcessor, ICliCommandProcessor
{
    public override string Command { get; set; } = "iam";
    public override string Description { get; set; } = "AWS IAM operations — users, roles, policies";

    public override IEnumerable<ICliCommandProcessor>? Processors { get; set; }

    public IamProcessor(AwsCredentialManager credentialManager)
    {
        Processors =
        [
            new IamUsersProcessor(credentialManager),
            new IamRolesProcessor(credentialManager),
            new IamPoliciesProcessor(credentialManager),
        ];
    }

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);
}
