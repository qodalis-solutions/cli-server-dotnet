using Amazon.EC2;
using Amazon.EC2.Model;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Plugin.Aws.Services;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Plugin.Aws.Processors;

// ---------------------------------------------------------------------------
// ec2 list
// ---------------------------------------------------------------------------

internal class Ec2ListProcessor : CliCommandProcessor, ICliCommandProcessor
{
    public override string Command { get; set; } = "list";
    public override string Description { get; set; } = "List all EC2 instances";

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

    public Ec2ListProcessor(AwsCredentialManager credentialManager) => _credentialManager = credentialManager;

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

// ---------------------------------------------------------------------------
// ec2 describe
// ---------------------------------------------------------------------------

internal class Ec2DescribeProcessor : CliCommandProcessor, ICliCommandProcessor
{
    public override string Command { get; set; } = "describe";
    public override string Description { get; set; } = "Describe an EC2 instance";
    public override bool? ValueRequired { get; set; } = true;

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

    public Ec2DescribeProcessor(AwsCredentialManager credentialManager) => _credentialManager = credentialManager;

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

// ---------------------------------------------------------------------------
// ec2 start
// ---------------------------------------------------------------------------

internal class Ec2StartProcessor : CliCommandProcessor, ICliCommandProcessor
{
    public override string Command { get; set; } = "start";
    public override string Description { get; set; } = "Start an EC2 instance";
    public override bool? ValueRequired { get; set; } = true;

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

    public Ec2StartProcessor(AwsCredentialManager credentialManager) => _credentialManager = credentialManager;

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

// ---------------------------------------------------------------------------
// ec2 stop
// ---------------------------------------------------------------------------

internal class Ec2StopProcessor : CliCommandProcessor, ICliCommandProcessor
{
    public override string Command { get; set; } = "stop";
    public override string Description { get; set; } = "Stop an EC2 instance";
    public override bool? ValueRequired { get; set; } = true;

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

    public Ec2StopProcessor(AwsCredentialManager credentialManager) => _credentialManager = credentialManager;

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

// ---------------------------------------------------------------------------
// ec2 reboot
// ---------------------------------------------------------------------------

internal class Ec2RebootProcessor : CliCommandProcessor, ICliCommandProcessor
{
    public override string Command { get; set; } = "reboot";
    public override string Description { get; set; } = "Reboot an EC2 instance";
    public override bool? ValueRequired { get; set; } = true;

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

    public Ec2RebootProcessor(AwsCredentialManager credentialManager) => _credentialManager = credentialManager;

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

// ---------------------------------------------------------------------------
// ec2 sg list
// ---------------------------------------------------------------------------

internal class Ec2SgListProcessor : CliCommandProcessor, ICliCommandProcessor
{
    public override string Command { get; set; } = "list";
    public override string Description { get; set; } = "List all security groups";

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

    public Ec2SgListProcessor(AwsCredentialManager credentialManager) => _credentialManager = credentialManager;

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

// ---------------------------------------------------------------------------
// ec2 sg (parent)
// ---------------------------------------------------------------------------

internal class Ec2SgProcessor : CliCommandProcessor, ICliCommandProcessor
{
    public override string Command { get; set; } = "sg";
    public override string Description { get; set; } = "EC2 security group operations";

    public override IEnumerable<ICliCommandProcessor>? Processors { get; set; }

    public Ec2SgProcessor(AwsCredentialManager credentialManager)
    {
        Processors = [new Ec2SgListProcessor(credentialManager)];
    }

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);
}

// ---------------------------------------------------------------------------
// ec2 (parent)
// ---------------------------------------------------------------------------

public class Ec2Processor : CliCommandProcessor, ICliCommandProcessor
{
    public override string Command { get; set; } = "ec2";
    public override string Description { get; set; } = "Amazon EC2 operations — manage instances and security groups";

    public override IEnumerable<ICliCommandProcessor>? Processors { get; set; }

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

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);
}
