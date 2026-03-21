using Amazon.SQS;
using Amazon.SQS.Model;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Plugin.Aws.Services;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Plugin.Aws.Processors;

// ---------------------------------------------------------------------------
// sqs list
// ---------------------------------------------------------------------------

internal class SqsListProcessor : CliCommandProcessor, ICliCommandProcessor
{
    public override string Command { get; set; } = "list";
    public override string Description { get; set; } = "List SQS queues";

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

    public SqsListProcessor(AwsCredentialManager credentialManager) => _credentialManager = credentialManager;

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    async Task<ICliStructuredResponse?> ICliCommandProcessor.HandleStructuredAsync(CliProcessCommand command, CancellationToken ct = default)
    {
        var builder = new CliResponseBuilder();
        var regionOverride = command.Args.TryGetValue("region", out var r) ? r.ToString() : null;
        var client = _credentialManager.GetClient<AmazonSQSClient>(regionOverride);

        try
        {
            var response = await client.ListQueuesAsync(new ListQueuesRequest(), ct);
            var queueUrls = response.QueueUrls ?? [];

            if (queueUrls.Count == 0)
            {
                builder.WriteText("No SQS queues found.", "warning");
                return builder.Build();
            }

            builder.WriteList(queueUrls.ToArray());
        }
        catch (Exception ex)
        {
            builder.WriteText($"Failed to list SQS queues: {ex.Message}", "error");
            builder.SetExitCode(1);
        }

        return builder.Build();
    }
}

// ---------------------------------------------------------------------------
// sqs send
// ---------------------------------------------------------------------------

internal class SqsSendProcessor : CliCommandProcessor, ICliCommandProcessor
{
    public override string Command { get; set; } = "send";
    public override string Description { get; set; } = "Send a message to an SQS queue";
    public override bool? ValueRequired { get; set; } = true;

    public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } =
    [
        new CliCommandParameterDescriptor
        {
            Name = "message",
            Aliases = ["-m"],
            Description = "Message body to send",
            Type = CommandParameterType.String,
            Required = true,
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

    public SqsSendProcessor(AwsCredentialManager credentialManager) => _credentialManager = credentialManager;

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    async Task<ICliStructuredResponse?> ICliCommandProcessor.HandleStructuredAsync(CliProcessCommand command, CancellationToken ct = default)
    {
        var builder = new CliResponseBuilder();
        var queueUrl = command.Value?.Trim();

        if (string.IsNullOrEmpty(queueUrl))
        {
            builder.WriteText("Queue URL is required. Usage: sqs send <queue-url> --message <body>", "error");
            builder.SetExitCode(1);
            return builder.Build();
        }

        var messageBody = command.Args.TryGetValue("message", out var m) ? m.ToString() : null;
        if (string.IsNullOrEmpty(messageBody))
        {
            builder.WriteText("--message is required. Usage: sqs send <queue-url> --message <body>", "error");
            builder.SetExitCode(1);
            return builder.Build();
        }

        var regionOverride = command.Args.TryGetValue("region", out var r) ? r.ToString() : null;
        var client = _credentialManager.GetClient<AmazonSQSClient>(regionOverride);

        try
        {
            var response = await client.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = queueUrl,
                MessageBody = messageBody,
            }, ct);

            builder.WriteText($"Message sent successfully. MessageId: {response.MessageId}", "success");
        }
        catch (Exception ex)
        {
            builder.WriteText($"Failed to send message: {ex.Message}", "error");
            builder.SetExitCode(1);
        }

        return builder.Build();
    }
}

// ---------------------------------------------------------------------------
// sqs receive
// ---------------------------------------------------------------------------

internal class SqsReceiveProcessor : CliCommandProcessor, ICliCommandProcessor
{
    public override string Command { get; set; } = "receive";
    public override string Description { get; set; } = "Receive messages from an SQS queue";
    public override bool? ValueRequired { get; set; } = true;

    public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } =
    [
        new CliCommandParameterDescriptor
        {
            Name = "max",
            Aliases = ["-n"],
            Description = "Maximum number of messages to receive (default: 1)",
            Type = CommandParameterType.Number,
            DefaultValue = "1",
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

    public SqsReceiveProcessor(AwsCredentialManager credentialManager) => _credentialManager = credentialManager;

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    async Task<ICliStructuredResponse?> ICliCommandProcessor.HandleStructuredAsync(CliProcessCommand command, CancellationToken ct = default)
    {
        var builder = new CliResponseBuilder();
        var queueUrl = command.Value?.Trim();

        if (string.IsNullOrEmpty(queueUrl))
        {
            builder.WriteText("Queue URL is required. Usage: sqs receive <queue-url>", "error");
            builder.SetExitCode(1);
            return builder.Build();
        }

        var max = 1;
        if (command.Args.TryGetValue("max", out var n) && int.TryParse(n.ToString(), out var parsedMax))
        {
            max = parsedMax;
        }

        var regionOverride = command.Args.TryGetValue("region", out var r) ? r.ToString() : null;
        var client = _credentialManager.GetClient<AmazonSQSClient>(regionOverride);

        try
        {
            var response = await client.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = max,
            }, ct);

            var messages = response.Messages ?? [];

            if (messages.Count == 0)
            {
                builder.WriteText("No messages available in the queue.", "warning");
                return builder.Build();
            }

            builder.WriteJson(messages.Select(msg => new
            {
                msg.MessageId,
                msg.ReceiptHandle,
                msg.Body,
            }).ToArray());
        }
        catch (Exception ex)
        {
            builder.WriteText($"Failed to receive messages: {ex.Message}", "error");
            builder.SetExitCode(1);
        }

        return builder.Build();
    }
}

// ---------------------------------------------------------------------------
// sqs purge
// ---------------------------------------------------------------------------

internal class SqsPurgeProcessor : CliCommandProcessor, ICliCommandProcessor
{
    public override string Command { get; set; } = "purge";
    public override string Description { get; set; } = "Purge all messages from an SQS queue";
    public override bool? ValueRequired { get; set; } = true;

    public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } =
    [
        new CliCommandParameterDescriptor
        {
            Name = "dry-run",
            Description = "Show what would be purged without actually purging",
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

    public SqsPurgeProcessor(AwsCredentialManager credentialManager) => _credentialManager = credentialManager;

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    async Task<ICliStructuredResponse?> ICliCommandProcessor.HandleStructuredAsync(CliProcessCommand command, CancellationToken ct = default)
    {
        var builder = new CliResponseBuilder();
        var queueUrl = command.Value?.Trim();

        if (string.IsNullOrEmpty(queueUrl))
        {
            builder.WriteText("Queue URL is required. Usage: sqs purge <queue-url>", "error");
            builder.SetExitCode(1);
            return builder.Build();
        }

        if (command.Args.ContainsKey("dry-run"))
        {
            builder.WriteText($"[dry-run] Would purge all messages from queue: {queueUrl}", "warning");
            return builder.Build();
        }

        var regionOverride = command.Args.TryGetValue("region", out var r) ? r.ToString() : null;
        var client = _credentialManager.GetClient<AmazonSQSClient>(regionOverride);

        try
        {
            await client.PurgeQueueAsync(new PurgeQueueRequest { QueueUrl = queueUrl }, ct);
            builder.WriteText($"Queue purged successfully: {queueUrl}", "success");
        }
        catch (Exception ex)
        {
            builder.WriteText($"Failed to purge queue: {ex.Message}", "error");
            builder.SetExitCode(1);
        }

        return builder.Build();
    }
}

// ---------------------------------------------------------------------------
// sqs (parent)
// ---------------------------------------------------------------------------

public class SqsProcessor : CliCommandProcessor, ICliCommandProcessor
{
    public override string Command { get; set; } = "sqs";
    public override string Description { get; set; } = "AWS SQS operations — list, send, receive, purge";

    public override IEnumerable<ICliCommandProcessor>? Processors { get; set; }

    public SqsProcessor(AwsCredentialManager credentialManager)
    {
        Processors =
        [
            new SqsListProcessor(credentialManager),
            new SqsSendProcessor(credentialManager),
            new SqsReceiveProcessor(credentialManager),
            new SqsPurgeProcessor(credentialManager),
        ];
    }

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);
}
