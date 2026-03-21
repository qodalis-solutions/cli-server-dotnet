using Amazon.CloudWatch;
using Amazon.CloudWatch.Model;
using Amazon.CloudWatchLogs;
using Amazon.CloudWatchLogs.Model;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Plugin.Aws.Services;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Plugin.Aws.Processors;

// ---------------------------------------------------------------------------
// cloudwatch alarms
// ---------------------------------------------------------------------------

internal class CloudWatchAlarmsProcessor : CliCommandProcessor, ICliCommandProcessor
{
    public override string Command { get; set; } = "alarms";
    public override string Description { get; set; } = "List CloudWatch alarms";

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

    public CloudWatchAlarmsProcessor(AwsCredentialManager credentialManager) => _credentialManager = credentialManager;

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    async Task<ICliStructuredResponse?> ICliCommandProcessor.HandleStructuredAsync(CliProcessCommand command, CancellationToken ct = default)
    {
        var builder = new CliResponseBuilder();
        var regionOverride = command.Args.TryGetValue("region", out var r) ? r.ToString() : null;
        var client = _credentialManager.GetClient<AmazonCloudWatchClient>(regionOverride);

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

// ---------------------------------------------------------------------------
// cloudwatch logs
// ---------------------------------------------------------------------------

internal class CloudWatchLogsProcessor : CliCommandProcessor, ICliCommandProcessor
{
    public override string Command { get; set; } = "logs";
    public override string Description { get; set; } = "Fetch log events from a CloudWatch log group";
    public override bool? ValueRequired { get; set; } = true;

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
    ];

    private readonly AwsCredentialManager _credentialManager;

    public CloudWatchLogsProcessor(AwsCredentialManager credentialManager) => _credentialManager = credentialManager;

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
        var client = _credentialManager.GetClient<AmazonCloudWatchLogsClient>(regionOverride);

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

// ---------------------------------------------------------------------------
// cloudwatch metrics
// ---------------------------------------------------------------------------

internal class CloudWatchMetricsProcessor : CliCommandProcessor, ICliCommandProcessor
{
    public override string Command { get; set; } = "metrics";
    public override string Description { get; set; } = "List CloudWatch metrics for a namespace";
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

    public CloudWatchMetricsProcessor(AwsCredentialManager credentialManager) => _credentialManager = credentialManager;

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
        var client = _credentialManager.GetClient<AmazonCloudWatchClient>(regionOverride);

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

// ---------------------------------------------------------------------------
// cloudwatch (parent)
// ---------------------------------------------------------------------------

public class CloudWatchProcessor : CliCommandProcessor, ICliCommandProcessor
{
    public override string Command { get; set; } = "cloudwatch";
    public override string Description { get; set; } = "Amazon CloudWatch — alarms, logs, and metrics";

    public override IEnumerable<ICliCommandProcessor>? Processors { get; set; }

    public CloudWatchProcessor(AwsCredentialManager credentialManager)
    {
        Processors =
        [
            new CloudWatchAlarmsProcessor(credentialManager),
            new CloudWatchLogsProcessor(credentialManager),
            new CloudWatchMetricsProcessor(credentialManager),
        ];
    }

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);
}
