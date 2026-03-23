using Amazon.CloudWatch;
using Amazon.CloudWatch.Model;
using Amazon.CloudWatchLogs;
using Amazon.CloudWatchLogs.Model;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Plugin.Aws.Services;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Plugin.Aws.Processors;

/// <summary>
/// Handles the "cloudwatch alarms" command to list CloudWatch metric alarms.
/// </summary>
internal class CloudWatchAlarmsProcessor : CliCommandProcessor, ICliCommandProcessor
{
    /// <inheritdoc />
    public override string Command { get; set; } = "alarms";

    /// <inheritdoc />
    public override string Description { get; set; } = "List CloudWatch alarms";

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
        new CliCommandParameterDescriptor
        {
            Name = "profile",
            Aliases = ["-p"],
            Description = "AWS profile name from ~/.aws/credentials",
            Type = CommandParameterType.String,
        },
    ];

    private readonly AwsCredentialManager _credentialManager;

    /// <summary>
    /// Initializes a new instance of <see cref="CloudWatchAlarmsProcessor"/>.
    /// </summary>
    /// <param name="credentialManager">The credential manager used to create the CloudWatch client.</param>
    public CloudWatchAlarmsProcessor(AwsCredentialManager credentialManager) => _credentialManager = credentialManager;

    /// <inheritdoc />
    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    async Task<ICliStructuredResponse?> ICliCommandProcessor.HandleStructuredAsync(CliProcessCommand command, CancellationToken ct = default)
    {
        var builder = new CliResponseBuilder();
        var regionOverride = command.Args.TryGetValue("region", out var r) ? r.ToString() : null;
        var profileOverride = command.Args.TryGetValue("profile", out var p) ? p.ToString() : null;
        var client = _credentialManager.GetClient<AmazonCloudWatchClient>(regionOverride, profileOverride);

        try
        {
            var response = await client.DescribeAlarmsAsync(new DescribeAlarmsRequest(), ct);
            var alarms = response.MetricAlarms ?? [];

            if (alarms.Count == 0)
            {
                builder.WriteText("No alarms found.", "warning");
                return builder.Build();
            }

            var headers = new[] { "Name", "State", "Metric", "Namespace" };
            var rows = alarms.Select(a => new[]
            {
                a.AlarmName ?? "(unknown)",
                a.StateValue?.Value ?? "(unknown)",
                a.MetricName ?? "(unknown)",
                a.Namespace ?? "(unknown)",
            }).ToArray();

            builder.WriteTable(headers, rows);
        }
        catch (Exception ex)
        {
            builder.WriteText($"Failed to list alarms: {ex.Message}", "error");
            builder.SetExitCode(1);
        }

        return builder.Build();
    }
}

/// <summary>
/// Handles the "cloudwatch logs" command to fetch log events from a CloudWatch Logs group.
/// </summary>
internal class CloudWatchLogsProcessor : CliCommandProcessor, ICliCommandProcessor
{
    /// <inheritdoc />
    public override string Command { get; set; } = "logs";

    /// <inheritdoc />
    public override string Description { get; set; } = "Fetch log events from a CloudWatch log group";

    /// <inheritdoc />
    public override bool? ValueRequired { get; set; } = true;

    /// <inheritdoc />
    public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } =
    [
        new CliCommandParameterDescriptor
        {
            Name = "filter",
            Aliases = ["-f"],
            Description = "Filter pattern for log events",
            Type = CommandParameterType.String,
        },
        new CliCommandParameterDescriptor
        {
            Name = "limit",
            Aliases = ["-l"],
            Description = "Maximum number of events to return (default: 50)",
            Type = CommandParameterType.Number,
            DefaultValue = "50",
        },
        new CliCommandParameterDescriptor
        {
            Name = "region",
            Aliases = ["-r"],
            Description = "AWS region override",
            Type = CommandParameterType.String,
        },
        new CliCommandParameterDescriptor
        {
            Name = "profile",
            Aliases = ["-p"],
            Description = "AWS profile name from ~/.aws/credentials",
            Type = CommandParameterType.String,
        },
    ];

    private readonly AwsCredentialManager _credentialManager;

    /// <summary>
    /// Initializes a new instance of <see cref="CloudWatchLogsProcessor"/>.
    /// </summary>
    /// <param name="credentialManager">The credential manager used to create the CloudWatch Logs client.</param>
    public CloudWatchLogsProcessor(AwsCredentialManager credentialManager) => _credentialManager = credentialManager;

    /// <inheritdoc />
    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    async Task<ICliStructuredResponse?> ICliCommandProcessor.HandleStructuredAsync(CliProcessCommand command, CancellationToken ct = default)
    {
        var builder = new CliResponseBuilder();
        var logGroupName = command.Value?.Trim();

        if (string.IsNullOrEmpty(logGroupName))
        {
            builder.WriteText("Log group name is required. Usage: cloudwatch logs <log-group>", "error");
            builder.SetExitCode(1);
            return builder.Build();
        }

        var regionOverride = command.Args.TryGetValue("region", out var r) ? r.ToString() : null;
        var profileOverride = command.Args.TryGetValue("profile", out var p) ? p.ToString() : null;
        var client = _credentialManager.GetClient<AmazonCloudWatchLogsClient>(regionOverride, profileOverride);

        var limit = 50;
        if (command.Args.TryGetValue("limit", out var l) && int.TryParse(l.ToString(), out var parsedLimit))
        {
            limit = parsedLimit;
        }

        var filterPattern = command.Args.TryGetValue("filter", out var f) ? f.ToString() : null;

        try
        {
            var request = new FilterLogEventsRequest
            {
                LogGroupName = logGroupName,
                Limit = limit,
            };

            if (!string.IsNullOrEmpty(filterPattern))
            {
                request.FilterPattern = filterPattern;
            }

            var response = await client.FilterLogEventsAsync(request, ct);
            var events = response.Events ?? [];

            if (events.Count == 0)
            {
                builder.WriteText("No log events found.", "warning");
                return builder.Build();
            }

            var lines = events.Select(e =>
            {
                var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(e.Timestamp).ToString("o");
                var message = e.Message?.TrimEnd() ?? "";
                return $"{timestamp} {message}";
            });

            builder.WriteText(string.Join("\n", lines));
        }
        catch (Exception ex)
        {
            builder.WriteText($"Failed to fetch log events: {ex.Message}", "error");
            builder.SetExitCode(1);
        }

        return builder.Build();
    }
}

/// <summary>
/// Handles the "cloudwatch metrics" command to list CloudWatch metrics for a given namespace.
/// </summary>
internal class CloudWatchMetricsProcessor : CliCommandProcessor, ICliCommandProcessor
{
    /// <inheritdoc />
    public override string Command { get; set; } = "metrics";

    /// <inheritdoc />
    public override string Description { get; set; } = "List CloudWatch metrics for a namespace";

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
        new CliCommandParameterDescriptor
        {
            Name = "profile",
            Aliases = ["-p"],
            Description = "AWS profile name from ~/.aws/credentials",
            Type = CommandParameterType.String,
        },
    ];

    private readonly AwsCredentialManager _credentialManager;

    /// <summary>
    /// Initializes a new instance of <see cref="CloudWatchMetricsProcessor"/>.
    /// </summary>
    /// <param name="credentialManager">The credential manager used to create the CloudWatch client.</param>
    public CloudWatchMetricsProcessor(AwsCredentialManager credentialManager) => _credentialManager = credentialManager;

    /// <inheritdoc />
    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    async Task<ICliStructuredResponse?> ICliCommandProcessor.HandleStructuredAsync(CliProcessCommand command, CancellationToken ct = default)
    {
        var builder = new CliResponseBuilder();
        var ns = command.Value?.Trim();

        if (string.IsNullOrEmpty(ns))
        {
            builder.WriteText("Namespace is required. Usage: cloudwatch metrics <namespace>", "error");
            builder.SetExitCode(1);
            return builder.Build();
        }

        var regionOverride = command.Args.TryGetValue("region", out var r) ? r.ToString() : null;
        var profileOverride = command.Args.TryGetValue("profile", out var p) ? p.ToString() : null;
        var client = _credentialManager.GetClient<AmazonCloudWatchClient>(regionOverride, profileOverride);

        try
        {
            var response = await client.ListMetricsAsync(new ListMetricsRequest
            {
                Namespace = ns,
            }, ct);

            var metrics = response.Metrics ?? [];

            if (metrics.Count == 0)
            {
                builder.WriteText($"No metrics found for namespace \"{ns}\".", "warning");
                return builder.Build();
            }

            var headers = new[] { "MetricName", "Dimensions" };
            var rows = metrics.Select(m =>
            {
                var dims = m.Dimensions != null
                    ? string.Join(", ", m.Dimensions.Select(d => $"{d.Name}={d.Value}"))
                    : "";
                return new[] { m.MetricName ?? "(unknown)", dims };
            }).ToArray();

            builder.WriteTable(headers, rows);
        }
        catch (Exception ex)
        {
            builder.WriteText($"Failed to list metrics: {ex.Message}", "error");
            builder.SetExitCode(1);
        }

        return builder.Build();
    }
}

/// <summary>
/// Parent processor for CloudWatch commands, aggregating alarms, logs, and metrics sub-commands.
/// </summary>
public class CloudWatchProcessor : CliCommandProcessor, ICliCommandProcessor
{
    /// <inheritdoc />
    public override string Command { get; set; } = "cloudwatch";

    /// <inheritdoc />
    public override string Description { get; set; } = "Amazon CloudWatch — alarms, logs, and metrics";

    /// <inheritdoc />
    public override IEnumerable<ICliCommandProcessor>? Processors { get; set; }

    /// <summary>
    /// Initializes a new instance of <see cref="CloudWatchProcessor"/> with its sub-command processors.
    /// </summary>
    /// <param name="credentialManager">The credential manager passed to child processors.</param>
    public CloudWatchProcessor(AwsCredentialManager credentialManager)
    {
        Processors =
        [
            new CloudWatchAlarmsProcessor(credentialManager),
            new CloudWatchLogsProcessor(credentialManager),
            new CloudWatchMetricsProcessor(credentialManager),
        ];
    }

    /// <inheritdoc />
    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);
}
