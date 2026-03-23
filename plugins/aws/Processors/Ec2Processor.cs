using Amazon.EC2;
using Amazon.EC2.Model;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Plugin.Aws.Services;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Plugin.Aws.Processors;

/// <summary>
/// Handles the "ec2 list" command to list all EC2 instances with key details.
/// </summary>
internal class Ec2ListProcessor : CliCommandProcessor, ICliCommandProcessor
{
    /// <inheritdoc />
    public override string Command { get; set; } = "list";

    /// <inheritdoc />
    public override string Description { get; set; } = "List all EC2 instances";

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
    /// Initializes a new instance of <see cref="Ec2ListProcessor"/>.
    /// </summary>
    /// <param name="credentialManager">The credential manager used to create the EC2 client.</param>
    public Ec2ListProcessor(AwsCredentialManager credentialManager) => _credentialManager = credentialManager;

    /// <inheritdoc />
    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    async Task<ICliStructuredResponse?> ICliCommandProcessor.HandleStructuredAsync(CliProcessCommand command, CancellationToken ct = default)
    {
        var builder = new CliResponseBuilder();
        var regionOverride = command.Args.TryGetValue("region", out var r) ? r.ToString() : null;
        var client = _credentialManager.GetClient<AmazonEC2Client>(regionOverride);

        try
        {
            var response = await client.DescribeInstancesAsync(new DescribeInstancesRequest(), ct);
            var instances = response.Reservations
                .SelectMany(res => res.Instances)
                .ToList();

            if (instances.Count == 0)
            {
                builder.WriteText("No instances found.", "warning");
                return builder.Build();
            }

            var headers = new[] { "Instance ID", "Name", "State", "Type", "Public IP" };
            var rows = instances.Select(inst =>
            {
                var nameTag = inst.Tags?.FirstOrDefault(t => t.Key == "Name");
                return new string[]
                {
                    inst.InstanceId ?? "(unknown)",
                    nameTag?.Value ?? "(none)",
                    inst.State?.Name?.Value ?? "(unknown)",
                    inst.InstanceType?.Value ?? "(unknown)",
                    inst.PublicIpAddress ?? "(none)",
                };
            }).ToArray();

            builder.WriteTable(headers, rows);
        }
        catch (Exception ex)
        {
            builder.WriteText($"Failed to list instances: {ex.Message}", "error");
            builder.SetExitCode(1);
        }

        return builder.Build();
    }
}

/// <summary>
/// Handles the "ec2 describe" command to show detailed information about a specific EC2 instance.
/// </summary>
internal class Ec2DescribeProcessor : CliCommandProcessor, ICliCommandProcessor
{
    /// <inheritdoc />
    public override string Command { get; set; } = "describe";

    /// <inheritdoc />
    public override string Description { get; set; } = "Describe an EC2 instance";

    /// <inheritdoc />
    public override bool? ValueRequired { get; set; } = true;

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
    /// Initializes a new instance of <see cref="Ec2DescribeProcessor"/>.
    /// </summary>
    /// <param name="credentialManager">The credential manager used to create the EC2 client.</param>
    public Ec2DescribeProcessor(AwsCredentialManager credentialManager) => _credentialManager = credentialManager;

    /// <inheritdoc />
    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    async Task<ICliStructuredResponse?> ICliCommandProcessor.HandleStructuredAsync(CliProcessCommand command, CancellationToken ct = default)
    {
        var builder = new CliResponseBuilder();
        var instanceId = command.Value?.Trim();

        if (string.IsNullOrEmpty(instanceId))
        {
            builder.WriteText("Instance ID is required. Usage: ec2 describe <instance-id>", "error");
            builder.SetExitCode(1);
            return builder.Build();
        }

        var regionOverride = command.Args.TryGetValue("region", out var r) ? r.ToString() : null;
        var client = _credentialManager.GetClient<AmazonEC2Client>(regionOverride);

        try
        {
            var response = await client.DescribeInstancesAsync(new DescribeInstancesRequest
            {
                InstanceIds = [instanceId],
            }, ct);

            var instances = response.Reservations.SelectMany(res => res.Instances).ToList();

            if (instances.Count == 0)
            {
                builder.WriteText($"Instance \"{instanceId}\" not found.", "error");
                builder.SetExitCode(1);
                return builder.Build();
            }

            var inst = instances[0];
            var nameTag = inst.Tags?.FirstOrDefault(t => t.Key == "Name");
            var sgList = inst.SecurityGroups != null && inst.SecurityGroups.Count > 0
                ? string.Join(", ", inst.SecurityGroups.Select(sg => $"{sg.GroupId} ({sg.GroupName})"))
                : "(none)";

            builder.WriteKeyValue(new Dictionary<string, string>
            {
                ["Instance ID"] = inst.InstanceId ?? "(unknown)",
                ["Name"] = nameTag?.Value ?? "(none)",
                ["State"] = inst.State?.Name ?? "(unknown)",
                ["Type"] = inst.InstanceType?.Value ?? "(unknown)",
                ["Availability Zone"] = inst.Placement?.AvailabilityZone ?? "(unknown)",
                ["Public IP"] = inst.PublicIpAddress ?? "(none)",
                ["Private IP"] = inst.PrivateIpAddress ?? "(none)",
                ["Launch Time"] = inst.LaunchTime != default ? inst.LaunchTime.ToString("o") : "(unknown)",
                ["Security Groups"] = sgList,
            });
        }
        catch (Exception ex)
        {
            builder.WriteText($"Failed to describe instance: {ex.Message}", "error");
            builder.SetExitCode(1);
        }

        return builder.Build();
    }
}

/// <summary>
/// Handles the "ec2 start" command to start a stopped EC2 instance.
/// </summary>
internal class Ec2StartProcessor : CliCommandProcessor, ICliCommandProcessor
{
    /// <inheritdoc />
    public override string Command { get; set; } = "start";

    /// <inheritdoc />
    public override string Description { get; set; } = "Start an EC2 instance";

    /// <inheritdoc />
    public override bool? ValueRequired { get; set; } = true;

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
    /// Initializes a new instance of <see cref="Ec2StartProcessor"/>.
    /// </summary>
    /// <param name="credentialManager">The credential manager used to create the EC2 client.</param>
    public Ec2StartProcessor(AwsCredentialManager credentialManager) => _credentialManager = credentialManager;

    /// <inheritdoc />
    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    async Task<ICliStructuredResponse?> ICliCommandProcessor.HandleStructuredAsync(CliProcessCommand command, CancellationToken ct = default)
    {
        var builder = new CliResponseBuilder();
        var instanceId = command.Value?.Trim();

        if (string.IsNullOrEmpty(instanceId))
        {
            builder.WriteText("Instance ID is required. Usage: ec2 start <instance-id>", "error");
            builder.SetExitCode(1);
            return builder.Build();
        }

        var regionOverride = command.Args.TryGetValue("region", out var r) ? r.ToString() : null;
        var client = _credentialManager.GetClient<AmazonEC2Client>(regionOverride);

        try
        {
            await client.StartInstancesAsync(new StartInstancesRequest
            {
                InstanceIds = [instanceId],
            }, ct);

            builder.WriteText($"Starting instance {instanceId}... done", "success");
        }
        catch (Exception ex)
        {
            builder.WriteText($"Failed to start instance: {ex.Message}", "error");
            builder.SetExitCode(1);
        }

        return builder.Build();
    }
}

/// <summary>
/// Handles the "ec2 stop" command to stop a running EC2 instance, with optional dry-run support.
/// </summary>
internal class Ec2StopProcessor : CliCommandProcessor, ICliCommandProcessor
{
    /// <inheritdoc />
    public override string Command { get; set; } = "stop";

    /// <inheritdoc />
    public override string Description { get; set; } = "Stop an EC2 instance";

    /// <inheritdoc />
    public override bool? ValueRequired { get; set; } = true;

    /// <inheritdoc />
    public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } =
    [
        new CliCommandParameterDescriptor
        {
            Name = "dry-run",
            Description = "Preview without stopping",
            Type = CommandParameterType.Boolean,
        },
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
    /// Initializes a new instance of <see cref="Ec2StopProcessor"/>.
    /// </summary>
    /// <param name="credentialManager">The credential manager used to create the EC2 client.</param>
    public Ec2StopProcessor(AwsCredentialManager credentialManager) => _credentialManager = credentialManager;

    /// <inheritdoc />
    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    async Task<ICliStructuredResponse?> ICliCommandProcessor.HandleStructuredAsync(CliProcessCommand command, CancellationToken ct = default)
    {
        var builder = new CliResponseBuilder();
        var instanceId = command.Value?.Trim();

        if (string.IsNullOrEmpty(instanceId))
        {
            builder.WriteText("Instance ID is required. Usage: ec2 stop <instance-id>", "error");
            builder.SetExitCode(1);
            return builder.Build();
        }

        if (command.Args.ContainsKey("dry-run"))
        {
            builder.WriteText($"[DRY RUN] Would stop instance {instanceId}", "warning");
            return builder.Build();
        }

        var regionOverride = command.Args.TryGetValue("region", out var r) ? r.ToString() : null;
        var client = _credentialManager.GetClient<AmazonEC2Client>(regionOverride);

        try
        {
            await client.StopInstancesAsync(new StopInstancesRequest
            {
                InstanceIds = [instanceId],
            }, ct);

            builder.WriteText($"Stopping instance {instanceId}... done", "success");
        }
        catch (Exception ex)
        {
            builder.WriteText($"Failed to stop instance: {ex.Message}", "error");
            builder.SetExitCode(1);
        }

        return builder.Build();
    }
}

/// <summary>
/// Handles the "ec2 reboot" command to reboot an EC2 instance, with optional dry-run support.
/// </summary>
internal class Ec2RebootProcessor : CliCommandProcessor, ICliCommandProcessor
{
    /// <inheritdoc />
    public override string Command { get; set; } = "reboot";

    /// <inheritdoc />
    public override string Description { get; set; } = "Reboot an EC2 instance";

    /// <inheritdoc />
    public override bool? ValueRequired { get; set; } = true;

    /// <inheritdoc />
    public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } =
    [
        new CliCommandParameterDescriptor
        {
            Name = "dry-run",
            Description = "Preview without rebooting",
            Type = CommandParameterType.Boolean,
        },
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
    /// Initializes a new instance of <see cref="Ec2RebootProcessor"/>.
    /// </summary>
    /// <param name="credentialManager">The credential manager used to create the EC2 client.</param>
    public Ec2RebootProcessor(AwsCredentialManager credentialManager) => _credentialManager = credentialManager;

    /// <inheritdoc />
    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    async Task<ICliStructuredResponse?> ICliCommandProcessor.HandleStructuredAsync(CliProcessCommand command, CancellationToken ct = default)
    {
        var builder = new CliResponseBuilder();
        var instanceId = command.Value?.Trim();

        if (string.IsNullOrEmpty(instanceId))
        {
            builder.WriteText("Instance ID is required. Usage: ec2 reboot <instance-id>", "error");
            builder.SetExitCode(1);
            return builder.Build();
        }

        if (command.Args.ContainsKey("dry-run"))
        {
            builder.WriteText($"[DRY RUN] Would reboot instance {instanceId}", "warning");
            return builder.Build();
        }

        var regionOverride = command.Args.TryGetValue("region", out var r) ? r.ToString() : null;
        var client = _credentialManager.GetClient<AmazonEC2Client>(regionOverride);

        try
        {
            await client.RebootInstancesAsync(new RebootInstancesRequest
            {
                InstanceIds = [instanceId],
            }, ct);

            builder.WriteText($"Rebooting instance {instanceId}... done", "success");
        }
        catch (Exception ex)
        {
            builder.WriteText($"Failed to reboot instance: {ex.Message}", "error");
            builder.SetExitCode(1);
        }

        return builder.Build();
    }
}

/// <summary>
/// Handles the "ec2 sg list" command to list all EC2 security groups.
/// </summary>
internal class Ec2SgListProcessor : CliCommandProcessor, ICliCommandProcessor
{
    /// <inheritdoc />
    public override string Command { get; set; } = "list";

    /// <inheritdoc />
    public override string Description { get; set; } = "List all security groups";

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
    /// Initializes a new instance of <see cref="Ec2SgListProcessor"/>.
    /// </summary>
    /// <param name="credentialManager">The credential manager used to create the EC2 client.</param>
    public Ec2SgListProcessor(AwsCredentialManager credentialManager) => _credentialManager = credentialManager;

    /// <inheritdoc />
    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    async Task<ICliStructuredResponse?> ICliCommandProcessor.HandleStructuredAsync(CliProcessCommand command, CancellationToken ct = default)
    {
        var builder = new CliResponseBuilder();
        var regionOverride = command.Args.TryGetValue("region", out var r) ? r.ToString() : null;
        var client = _credentialManager.GetClient<AmazonEC2Client>(regionOverride);

        try
        {
            var response = await client.DescribeSecurityGroupsAsync(new DescribeSecurityGroupsRequest(), ct);
            var groups = response.SecurityGroups ?? [];

            if (groups.Count == 0)
            {
                builder.WriteText("No security groups found.", "warning");
                return builder.Build();
            }

            var headers = new[] { "Group ID", "Group Name", "VPC ID", "Description" };
            var rows = groups.Select(sg => new[]
            {
                sg.GroupId ?? "(unknown)",
                sg.GroupName ?? "(unknown)",
                sg.VpcId ?? "(none)",
                sg.Description ?? "(none)",
            }).ToArray();

            builder.WriteTable(headers, rows);
        }
        catch (Exception ex)
        {
            builder.WriteText($"Failed to list security groups: {ex.Message}", "error");
            builder.SetExitCode(1);
        }

        return builder.Build();
    }
}

/// <summary>
/// Parent processor for the "ec2 sg" command group, aggregating security group sub-commands.
/// </summary>
internal class Ec2SgProcessor : CliCommandProcessor, ICliCommandProcessor
{
    /// <inheritdoc />
    public override string Command { get; set; } = "sg";

    /// <inheritdoc />
    public override string Description { get; set; } = "EC2 security group operations";

    /// <inheritdoc />
    public override IEnumerable<ICliCommandProcessor>? Processors { get; set; }

    /// <summary>
    /// Initializes a new instance of <see cref="Ec2SgProcessor"/> with its sub-command processors.
    /// </summary>
    /// <param name="credentialManager">The credential manager passed to child processors.</param>
    public Ec2SgProcessor(AwsCredentialManager credentialManager)
    {
        Processors = [new Ec2SgListProcessor(credentialManager)];
    }

    /// <inheritdoc />
    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);
}

/// <summary>
/// Parent processor for EC2 commands, aggregating instance and security group sub-commands.
/// </summary>
public class Ec2Processor : CliCommandProcessor, ICliCommandProcessor
{
    /// <inheritdoc />
    public override string Command { get; set; } = "ec2";

    /// <inheritdoc />
    public override string Description { get; set; } = "Amazon EC2 operations — manage instances and security groups";

    /// <inheritdoc />
    public override IEnumerable<ICliCommandProcessor>? Processors { get; set; }

    /// <summary>
    /// Initializes a new instance of <see cref="Ec2Processor"/> with its sub-command processors.
    /// </summary>
    /// <param name="credentialManager">The credential manager passed to child processors.</param>
    public Ec2Processor(AwsCredentialManager credentialManager)
    {
        Processors =
        [
            new Ec2ListProcessor(credentialManager),
            new Ec2DescribeProcessor(credentialManager),
            new Ec2StartProcessor(credentialManager),
            new Ec2StopProcessor(credentialManager),
            new Ec2RebootProcessor(credentialManager),
            new Ec2SgProcessor(credentialManager),
        ];
    }

    /// <inheritdoc />
    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);
}
