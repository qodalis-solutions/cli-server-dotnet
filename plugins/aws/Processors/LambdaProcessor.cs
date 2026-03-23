using Amazon.Lambda;
using Amazon.Lambda.Model;
using Amazon.CloudWatchLogs;
using Amazon.CloudWatchLogs.Model;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Plugin.Aws.Services;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Plugin.Aws.Processors;

/// <summary>
/// Handles the "lambda list" command to list all Lambda functions in the account.
/// </summary>
internal class LambdaListProcessor : CliCommandProcessor, ICliCommandProcessor
{
    /// <inheritdoc />
    public override string Command { get; set; } = "list";

    /// <inheritdoc />
    public override string Description { get; set; } = "List Lambda functions";

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
    /// Initializes a new instance of <see cref="LambdaListProcessor"/>.
    /// </summary>
    /// <param name="credentialManager">The credential manager used to create the Lambda client.</param>
    public LambdaListProcessor(AwsCredentialManager credentialManager) => _credentialManager = credentialManager;

    /// <inheritdoc />
    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    async Task<ICliStructuredResponse?> ICliCommandProcessor.HandleStructuredAsync(CliProcessCommand command, CancellationToken ct = default)
    {
        var builder = new CliResponseBuilder();
        var regionOverride = command.Args.TryGetValue("region", out var r) ? r.ToString() : null;
        var client = _credentialManager.GetClient<AmazonLambdaClient>(regionOverride);

        try
        {
            var response = await client.ListFunctionsAsync(new ListFunctionsRequest(), ct);
            var functions = response.Functions ?? [];

            if (functions.Count == 0)
            {
                builder.WriteText("No Lambda functions found.", "warning");
                return builder.Build();
            }

            var headers = new[] { "Name", "Runtime", "Memory (MB)", "Last Modified" };
            var rows = functions.Select(fn => new[]
            {
                fn.FunctionName ?? "(unknown)",
                fn.Runtime?.Value ?? "(unknown)",
                fn.MemorySize > 0 ? fn.MemorySize.ToString() : "(unknown)",
                fn.LastModified ?? "(unknown)",
            }).ToArray();

            builder.WriteTable(headers, rows);
        }
        catch (Exception ex)
        {
            builder.WriteText($"Failed to list Lambda functions: {ex.Message}", "error");
            builder.SetExitCode(1);
        }

        return builder.Build();
    }
}

/// <summary>
/// Handles the "lambda invoke" command to invoke a Lambda function with an optional JSON payload.
/// </summary>
internal class LambdaInvokeProcessor : CliCommandProcessor, ICliCommandProcessor
{
    /// <inheritdoc />
    public override string Command { get; set; } = "invoke";

    /// <inheritdoc />
    public override string Description { get; set; } = "Invoke a Lambda function";

    /// <inheritdoc />
    public override bool? ValueRequired { get; set; } = true;

    /// <inheritdoc />
    public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } =
    [
        new CliCommandParameterDescriptor
        {
            Name = "payload",
            Aliases = ["-p"],
            Description = "JSON payload to send to the function",
            Type = CommandParameterType.String,
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
    /// Initializes a new instance of <see cref="LambdaInvokeProcessor"/>.
    /// </summary>
    /// <param name="credentialManager">The credential manager used to create the Lambda client.</param>
    public LambdaInvokeProcessor(AwsCredentialManager credentialManager) => _credentialManager = credentialManager;

    /// <inheritdoc />
    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    async Task<ICliStructuredResponse?> ICliCommandProcessor.HandleStructuredAsync(CliProcessCommand command, CancellationToken ct = default)
    {
        var builder = new CliResponseBuilder();
        var functionName = command.Value?.Trim();

        if (string.IsNullOrEmpty(functionName))
        {
            builder.WriteText("Function name is required. Usage: lambda invoke <function-name>", "error");
            builder.SetExitCode(1);
            return builder.Build();
        }

        var regionOverride = command.Args.TryGetValue("region", out var r) ? r.ToString() : null;
        var client = _credentialManager.GetClient<AmazonLambdaClient>(regionOverride);

        var payloadStr = command.Args.TryGetValue("payload", out var p) ? p.ToString() : null;

        try
        {
            var request = new Amazon.Lambda.Model.InvokeRequest
            {
                FunctionName = functionName,
            };

            if (!string.IsNullOrEmpty(payloadStr))
            {
                request.Payload = payloadStr;
            }

            var response = await client.InvokeAsync(request, ct);

            if (!string.IsNullOrEmpty(response.FunctionError))
            {
                using var errorReader = new StreamReader(response.Payload);
                var errorBody = await errorReader.ReadToEndAsync(ct);
                builder.WriteText($"Function error ({response.FunctionError}): {errorBody}", "error");
                builder.SetExitCode(1);
                return builder.Build();
            }

            using var reader = new StreamReader(response.Payload);
            var resultBody = await reader.ReadToEndAsync(ct);

            builder.WriteJson(new
            {
                StatusCode = response.StatusCode,
                ExecutedVersion = response.ExecutedVersion ?? "(unknown)",
                Result = resultBody,
            });
        }
        catch (Exception ex)
        {
            builder.WriteText($"Failed to invoke function: {ex.Message}", "error");
            builder.SetExitCode(1);
        }

        return builder.Build();
    }
}

/// <summary>
/// Handles the "lambda logs" command to fetch recent CloudWatch log events for a Lambda function.
/// </summary>
internal class LambdaLogsProcessor : CliCommandProcessor, ICliCommandProcessor
{
    /// <inheritdoc />
    public override string Command { get; set; } = "logs";

    /// <inheritdoc />
    public override string Description { get; set; } = "View recent logs for a Lambda function";

    /// <inheritdoc />
    public override bool? ValueRequired { get; set; } = true;

    /// <inheritdoc />
    public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } =
    [
        new CliCommandParameterDescriptor
        {
            Name = "limit",
            Aliases = ["-l"],
            Description = "Maximum number of log events (default: 50)",
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

    /// <summary>
    /// Initializes a new instance of <see cref="LambdaLogsProcessor"/>.
    /// </summary>
    /// <param name="credentialManager">The credential manager used to create the CloudWatch Logs client.</param>
    public LambdaLogsProcessor(AwsCredentialManager credentialManager) => _credentialManager = credentialManager;

    /// <inheritdoc />
    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    async Task<ICliStructuredResponse?> ICliCommandProcessor.HandleStructuredAsync(CliProcessCommand command, CancellationToken ct = default)
    {
        var builder = new CliResponseBuilder();
        var functionName = command.Value?.Trim();

        if (string.IsNullOrEmpty(functionName))
        {
            builder.WriteText("Function name is required. Usage: lambda logs <function-name>", "error");
            builder.SetExitCode(1);
            return builder.Build();
        }

        var limit = 50;
        if (command.Args.TryGetValue("limit", out var l) && int.TryParse(l.ToString(), out var parsedLimit))
        {
            limit = parsedLimit;
        }

        if (limit <= 0)
        {
            builder.WriteText("--limit must be a positive number.", "error");
            builder.SetExitCode(1);
            return builder.Build();
        }

        var regionOverride = command.Args.TryGetValue("region", out var r) ? r.ToString() : null;
        var client = _credentialManager.GetClient<AmazonCloudWatchLogsClient>(regionOverride);

        var logGroupName = $"/aws/lambda/{functionName}";

        try
        {
            var response = await client.FilterLogEventsAsync(new FilterLogEventsRequest
            {
                LogGroupName = logGroupName,
                Limit = limit,
            }, ct);

            var events = response.Events ?? [];

            if (events.Count == 0)
            {
                builder.WriteText($"No log events found for {logGroupName}.", "warning");
                return builder.Build();
            }

            var lines = events.Select(e =>
            {
                var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(e.Timestamp)
                    .ToString("yyyy-MM-dd HH:mm:ss");
                var message = e.Message?.TrimEnd() ?? "";
                return $"{timestamp}  {message}";
            });

            builder.WriteText(string.Join("\n", lines));
        }
        catch (Exception ex)
        {
            builder.WriteText($"Failed to fetch logs: {ex.Message}", "error");
            builder.SetExitCode(1);
        }

        return builder.Build();
    }
}

/// <summary>
/// Parent processor for Lambda commands, aggregating list, invoke, and logs sub-commands.
/// </summary>
public class LambdaProcessor : CliCommandProcessor, ICliCommandProcessor
{
    /// <inheritdoc />
    public override string Command { get; set; } = "lambda";

    /// <inheritdoc />
    public override string Description { get; set; } = "AWS Lambda operations — list, invoke, and view logs";

    /// <inheritdoc />
    public override IEnumerable<ICliCommandProcessor>? Processors { get; set; }

    /// <summary>
    /// Initializes a new instance of <see cref="LambdaProcessor"/> with its sub-command processors.
    /// </summary>
    /// <param name="credentialManager">The credential manager passed to child processors.</param>
    public LambdaProcessor(AwsCredentialManager credentialManager)
    {
        Processors =
        [
            new LambdaListProcessor(credentialManager),
            new LambdaInvokeProcessor(credentialManager),
            new LambdaLogsProcessor(credentialManager),
        ];
    }

    /// <inheritdoc />
    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);
}
