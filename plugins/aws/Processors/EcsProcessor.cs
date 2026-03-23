using Amazon.ECS;
using Amazon.ECS.Model;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Plugin.Aws.Services;
using Qodalis.Cli.Services;
using Task = System.Threading.Tasks.Task;

namespace Qodalis.Cli.Plugin.Aws.Processors;

/// <summary>
/// Handles the "ecs clusters" command to list all ECS clusters with their task counts.
/// </summary>
internal class EcsClustersProcessor : CliCommandProcessor, ICliCommandProcessor
{
    /// <inheritdoc />
    public override string Command { get; set; } = "clusters";

    /// <inheritdoc />
    public override string Description { get; set; } = "List ECS clusters";

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
    /// Initializes a new instance of <see cref="EcsClustersProcessor"/>.
    /// </summary>
    /// <param name="credentialManager">The credential manager used to create the ECS client.</param>
    public EcsClustersProcessor(AwsCredentialManager credentialManager) => _credentialManager = credentialManager;

    /// <inheritdoc />
    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    async Task<ICliStructuredResponse?> ICliCommandProcessor.HandleStructuredAsync(CliProcessCommand command, CancellationToken ct = default)
    {
        var builder = new CliResponseBuilder();
        var regionOverride = command.Args.TryGetValue("region", out var r) ? r.ToString() : null;
        var client = _credentialManager.GetClient<AmazonECSClient>(regionOverride);

        try
        {
            var listResponse = await client.ListClustersAsync(new ListClustersRequest(), ct);
            var clusterArns = listResponse.ClusterArns ?? [];

            if (clusterArns.Count == 0)
            {
                builder.WriteText("No ECS clusters found.", "warning");
                return builder.Build();
            }

            var describeResponse = await client.DescribeClustersAsync(new DescribeClustersRequest
            {
                Clusters = clusterArns,
            }, ct);

            var clusters = describeResponse.Clusters ?? [];

            var headers = new[] { "Cluster Name", "Status", "Running Tasks", "Pending Tasks" };
            var rows = clusters.Select(cluster => new[]
            {
                cluster.ClusterName ?? "(unknown)",
                cluster.Status ?? "(unknown)",
                (cluster.RunningTasksCount).ToString(),
                (cluster.PendingTasksCount).ToString(),
            }).ToArray();

            builder.WriteTable(headers, rows);
        }
        catch (Exception ex)
        {
            builder.WriteText($"Failed to list ECS clusters: {ex.Message}", "error");
            builder.SetExitCode(1);
        }

        return builder.Build();
    }
}

/// <summary>
/// Handles the "ecs services" command to list ECS services within a specified cluster.
/// </summary>
internal class EcsServicesProcessor : CliCommandProcessor, ICliCommandProcessor
{
    /// <inheritdoc />
    public override string Command { get; set; } = "services";

    /// <inheritdoc />
    public override string Description { get; set; } = "List ECS services in a cluster";

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
    /// Initializes a new instance of <see cref="EcsServicesProcessor"/>.
    /// </summary>
    /// <param name="credentialManager">The credential manager used to create the ECS client.</param>
    public EcsServicesProcessor(AwsCredentialManager credentialManager) => _credentialManager = credentialManager;

    /// <inheritdoc />
    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    async Task<ICliStructuredResponse?> ICliCommandProcessor.HandleStructuredAsync(CliProcessCommand command, CancellationToken ct = default)
    {
        var builder = new CliResponseBuilder();
        var cluster = command.Value?.Trim();

        if (string.IsNullOrEmpty(cluster))
        {
            builder.WriteText("Cluster name or ARN is required. Usage: ecs services <cluster>", "error");
            builder.SetExitCode(1);
            return builder.Build();
        }

        var regionOverride = command.Args.TryGetValue("region", out var r) ? r.ToString() : null;
        var client = _credentialManager.GetClient<AmazonECSClient>(regionOverride);

        try
        {
            var listResponse = await client.ListServicesAsync(new ListServicesRequest
            {
                Cluster = cluster,
            }, ct);

            var serviceArns = listResponse.ServiceArns ?? [];

            if (serviceArns.Count == 0)
            {
                builder.WriteText($"No ECS services found in cluster \"{cluster}\".", "warning");
                return builder.Build();
            }

            var describeResponse = await client.DescribeServicesAsync(new DescribeServicesRequest
            {
                Cluster = cluster,
                Services = serviceArns,
            }, ct);

            var services = describeResponse.Services ?? [];

            var headers = new[] { "Service Name", "Status", "Desired", "Running", "Pending" };
            var rows = services.Select(service => new[]
            {
                service.ServiceName ?? "(unknown)",
                service.Status ?? "(unknown)",
                (service.DesiredCount).ToString(),
                (service.RunningCount).ToString(),
                (service.PendingCount).ToString(),
            }).ToArray();

            builder.WriteTable(headers, rows);
        }
        catch (Exception ex)
        {
            builder.WriteText($"Failed to list ECS services: {ex.Message}", "error");
            builder.SetExitCode(1);
        }

        return builder.Build();
    }
}

/// <summary>
/// Handles the "ecs tasks" command to list ECS tasks within a specified cluster.
/// </summary>
internal class EcsTasksProcessor : CliCommandProcessor, ICliCommandProcessor
{
    /// <inheritdoc />
    public override string Command { get; set; } = "tasks";

    /// <inheritdoc />
    public override string Description { get; set; } = "List ECS tasks in a cluster";

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
    /// Initializes a new instance of <see cref="EcsTasksProcessor"/>.
    /// </summary>
    /// <param name="credentialManager">The credential manager used to create the ECS client.</param>
    public EcsTasksProcessor(AwsCredentialManager credentialManager) => _credentialManager = credentialManager;

    /// <inheritdoc />
    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    async Task<ICliStructuredResponse?> ICliCommandProcessor.HandleStructuredAsync(CliProcessCommand command, CancellationToken ct = default)
    {
        var builder = new CliResponseBuilder();
        var cluster = command.Value?.Trim();

        if (string.IsNullOrEmpty(cluster))
        {
            builder.WriteText("Cluster name or ARN is required. Usage: ecs tasks <cluster>", "error");
            builder.SetExitCode(1);
            return builder.Build();
        }

        var regionOverride = command.Args.TryGetValue("region", out var r) ? r.ToString() : null;
        var client = _credentialManager.GetClient<AmazonECSClient>(regionOverride);

        try
        {
            var listResponse = await client.ListTasksAsync(new ListTasksRequest
            {
                Cluster = cluster,
            }, ct);

            var taskArns = listResponse.TaskArns ?? [];

            if (taskArns.Count == 0)
            {
                builder.WriteText($"No ECS tasks found in cluster \"{cluster}\".", "warning");
                return builder.Build();
            }

            var describeResponse = await client.DescribeTasksAsync(new DescribeTasksRequest
            {
                Cluster = cluster,
                Tasks = taskArns,
            }, ct);

            var tasks = describeResponse.Tasks ?? [];

            var headers = new[] { "Task ID", "Status", "Definition", "Started" };
            var rows = tasks.Select(task => new[]
            {
                task.TaskArn?.Split('/').LastOrDefault() ?? "(unknown)",
                task.LastStatus ?? "(unknown)",
                task.TaskDefinitionArn?.Split('/').LastOrDefault() ?? "(unknown)",
                task.StartedAt != default ? task.StartedAt.ToString("yyyy-MM-dd HH:mm:ss") : "(not started)",
            }).ToArray();

            builder.WriteTable(headers, rows);
        }
        catch (Exception ex)
        {
            builder.WriteText($"Failed to list ECS tasks: {ex.Message}", "error");
            builder.SetExitCode(1);
        }

        return builder.Build();
    }
}

/// <summary>
/// Parent processor for ECS commands, aggregating clusters, services, and tasks sub-commands.
/// </summary>
public class EcsProcessor : CliCommandProcessor, ICliCommandProcessor
{
    /// <inheritdoc />
    public override string Command { get; set; } = "ecs";

    /// <inheritdoc />
    public override string Description { get; set; } = "AWS ECS operations — clusters, services, tasks";

    /// <inheritdoc />
    public override IEnumerable<ICliCommandProcessor>? Processors { get; set; }

    /// <summary>
    /// Initializes a new instance of <see cref="EcsProcessor"/> with its sub-command processors.
    /// </summary>
    /// <param name="credentialManager">The credential manager passed to child processors.</param>
    public EcsProcessor(AwsCredentialManager credentialManager)
    {
        Processors =
        [
            new EcsClustersProcessor(credentialManager),
            new EcsServicesProcessor(credentialManager),
            new EcsTasksProcessor(credentialManager),
        ];
    }

    /// <inheritdoc />
    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);
}
