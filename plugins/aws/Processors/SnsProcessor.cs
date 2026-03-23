using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Plugin.Aws.Services;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Plugin.Aws.Processors;

/// <summary>
/// Handles the "sns topics" command to list all SNS topics in the account.
/// </summary>
internal class SnsTopicsProcessor : CliCommandProcessor, ICliCommandProcessor
{
    /// <inheritdoc />
    public override string Command { get; set; } = "topics";

    /// <inheritdoc />
    public override string Description { get; set; } = "List SNS topics";

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
    /// Initializes a new instance of <see cref="SnsTopicsProcessor"/>.
    /// </summary>
    /// <param name="credentialManager">The credential manager used to create the SNS client.</param>
    public SnsTopicsProcessor(AwsCredentialManager credentialManager) => _credentialManager = credentialManager;

    /// <inheritdoc />
    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    async Task<ICliStructuredResponse?> ICliCommandProcessor.HandleStructuredAsync(CliProcessCommand command, CancellationToken ct = default)
    {
        var builder = new CliResponseBuilder();
        var regionOverride = command.Args.TryGetValue("region", out var r) ? r.ToString() : null;
        var client = _credentialManager.GetClient<AmazonSimpleNotificationServiceClient>(regionOverride);

        try
        {
            var response = await client.ListTopicsAsync(new ListTopicsRequest(), ct);
            var topics = response.Topics ?? [];

            if (topics.Count == 0)
            {
                builder.WriteText("No SNS topics found.", "warning");
                return builder.Build();
            }

            var arns = topics.Select(t => t.TopicArn ?? "(unknown)").ToArray();
            builder.WriteList(arns);
        }
        catch (Exception ex)
        {
            builder.WriteText($"Failed to list SNS topics: {ex.Message}", "error");
            builder.SetExitCode(1);
        }

        return builder.Build();
    }
}

/// <summary>
/// Handles the "sns publish" command to publish a message to an SNS topic.
/// </summary>
internal class SnsPublishProcessor : CliCommandProcessor, ICliCommandProcessor
{
    /// <inheritdoc />
    public override string Command { get; set; } = "publish";

    /// <inheritdoc />
    public override string Description { get; set; } = "Publish a message to an SNS topic";

    /// <inheritdoc />
    public override bool? ValueRequired { get; set; } = true;

    /// <inheritdoc />
    public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } =
    [
        new CliCommandParameterDescriptor
        {
            Name = "message",
            Aliases = ["-m"],
            Description = "Message to publish",
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

    /// <summary>
    /// Initializes a new instance of <see cref="SnsPublishProcessor"/>.
    /// </summary>
    /// <param name="credentialManager">The credential manager used to create the SNS client.</param>
    public SnsPublishProcessor(AwsCredentialManager credentialManager) => _credentialManager = credentialManager;

    /// <inheritdoc />
    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    async Task<ICliStructuredResponse?> ICliCommandProcessor.HandleStructuredAsync(CliProcessCommand command, CancellationToken ct = default)
    {
        var builder = new CliResponseBuilder();
        var topicArn = command.Value?.Trim();

        if (string.IsNullOrEmpty(topicArn))
        {
            builder.WriteText("Topic ARN is required. Usage: sns publish <topic-arn> --message <message>", "error");
            builder.SetExitCode(1);
            return builder.Build();
        }

        var message = command.Args.TryGetValue("message", out var m) ? m.ToString() : null;
        if (string.IsNullOrEmpty(message))
        {
            builder.WriteText("--message is required. Usage: sns publish <topic-arn> --message <message>", "error");
            builder.SetExitCode(1);
            return builder.Build();
        }

        var regionOverride = command.Args.TryGetValue("region", out var r) ? r.ToString() : null;
        var client = _credentialManager.GetClient<AmazonSimpleNotificationServiceClient>(regionOverride);

        try
        {
            var response = await client.PublishAsync(new PublishRequest
            {
                TopicArn = topicArn,
                Message = message,
            }, ct);

            builder.WriteText($"Message published successfully. MessageId: {response.MessageId ?? "(unknown)"}", "success");
        }
        catch (Exception ex)
        {
            builder.WriteText($"Failed to publish message: {ex.Message}", "error");
            builder.SetExitCode(1);
        }

        return builder.Build();
    }
}

/// <summary>
/// Handles the "sns subscriptions" command to list SNS subscriptions, optionally filtered by topic ARN.
/// </summary>
internal class SnsSubscriptionsProcessor : CliCommandProcessor, ICliCommandProcessor
{
    /// <inheritdoc />
    public override string Command { get; set; } = "subscriptions";

    /// <inheritdoc />
    public override string Description { get; set; } = "List SNS subscriptions (optionally filtered by topic ARN)";

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
    /// Initializes a new instance of <see cref="SnsSubscriptionsProcessor"/>.
    /// </summary>
    /// <param name="credentialManager">The credential manager used to create the SNS client.</param>
    public SnsSubscriptionsProcessor(AwsCredentialManager credentialManager) => _credentialManager = credentialManager;

    /// <inheritdoc />
    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    async Task<ICliStructuredResponse?> ICliCommandProcessor.HandleStructuredAsync(CliProcessCommand command, CancellationToken ct = default)
    {
        var builder = new CliResponseBuilder();
        var topicArn = command.Value?.Trim();
        var regionOverride = command.Args.TryGetValue("region", out var r) ? r.ToString() : null;
        var client = _credentialManager.GetClient<AmazonSimpleNotificationServiceClient>(regionOverride);

        try
        {
            List<Subscription> subscriptions;

            if (!string.IsNullOrEmpty(topicArn))
            {
                var response = await client.ListSubscriptionsByTopicAsync(new ListSubscriptionsByTopicRequest
                {
                    TopicArn = topicArn,
                }, ct);
                subscriptions = response.Subscriptions ?? [];
            }
            else
            {
                var response = await client.ListSubscriptionsAsync(new ListSubscriptionsRequest(), ct);
                subscriptions = response.Subscriptions ?? [];
            }

            if (subscriptions.Count == 0)
            {
                builder.WriteText("No SNS subscriptions found.", "warning");
                return builder.Build();
            }

            var headers = new[] { "SubscriptionArn", "Protocol", "Endpoint", "TopicArn" };
            var rows = subscriptions.Select(sub => new[]
            {
                sub.SubscriptionArn ?? "(unknown)",
                sub.Protocol ?? "(unknown)",
                sub.Endpoint ?? "(unknown)",
                sub.TopicArn ?? "(unknown)",
            }).ToArray();

            builder.WriteTable(headers, rows);
        }
        catch (Exception ex)
        {
            builder.WriteText($"Failed to list SNS subscriptions: {ex.Message}", "error");
            builder.SetExitCode(1);
        }

        return builder.Build();
    }
}

/// <summary>
/// Parent processor for SNS commands, aggregating topics, publish, and subscriptions sub-commands.
/// </summary>
public class SnsProcessor : CliCommandProcessor, ICliCommandProcessor
{
    /// <inheritdoc />
    public override string Command { get; set; } = "sns";

    /// <inheritdoc />
    public override string Description { get; set; } = "AWS SNS operations — topics, publish, subscriptions";

    /// <inheritdoc />
    public override IEnumerable<ICliCommandProcessor>? Processors { get; set; }

    /// <summary>
    /// Initializes a new instance of <see cref="SnsProcessor"/> with its sub-command processors.
    /// </summary>
    /// <param name="credentialManager">The credential manager passed to child processors.</param>
    public SnsProcessor(AwsCredentialManager credentialManager)
    {
        Processors =
        [
            new SnsTopicsProcessor(credentialManager),
            new SnsPublishProcessor(credentialManager),
            new SnsSubscriptionsProcessor(credentialManager),
        ];
    }

    /// <inheritdoc />
    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);
}
