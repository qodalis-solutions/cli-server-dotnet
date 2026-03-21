using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Plugin.Aws.Services;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Plugin.Aws.Processors;

// ---------------------------------------------------------------------------
// dynamodb tables
// ---------------------------------------------------------------------------

internal class DynamoDbTablesProcessor : CliCommandProcessor, ICliCommandProcessor
{
    public override string Command { get; set; } = "tables";
    public override string Description { get; set; } = "List DynamoDB tables";

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

    public DynamoDbTablesProcessor(AwsCredentialManager credentialManager) => _credentialManager = credentialManager;

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    async Task<ICliStructuredResponse?> ICliCommandProcessor.HandleStructuredAsync(CliProcessCommand command, CancellationToken ct = default)
    {
        var builder = new CliResponseBuilder();
        var regionOverride = command.Args.TryGetValue("region", out var r) ? r.ToString() : null;
        var client = _credentialManager.GetClient<AmazonDynamoDBClient>(regionOverride);

        try
        {
            var response = await client.ListTablesAsync(new ListTablesRequest(), ct);
            var tableNames = response.TableNames ?? [];

            if (tableNames.Count == 0)
            {
                builder.WriteText("No DynamoDB tables found.", "warning");
                return builder.Build();
            }

            builder.WriteList(tableNames.ToArray());
        }
        catch (Exception ex)
        {
            builder.WriteText($"Failed to list DynamoDB tables: {ex.Message}", "error");
            builder.SetExitCode(1);
        }

        return builder.Build();
    }
}

// ---------------------------------------------------------------------------
// dynamodb describe
// ---------------------------------------------------------------------------

internal class DynamoDbDescribeProcessor : CliCommandProcessor, ICliCommandProcessor
{
    public override string Command { get; set; } = "describe";
    public override string Description { get; set; } = "Describe a DynamoDB table";
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

    public DynamoDbDescribeProcessor(AwsCredentialManager credentialManager) => _credentialManager = credentialManager;

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    async Task<ICliStructuredResponse?> ICliCommandProcessor.HandleStructuredAsync(CliProcessCommand command, CancellationToken ct = default)
    {
        var builder = new CliResponseBuilder();
        var tableName = command.Value?.Trim();

        if (string.IsNullOrEmpty(tableName))
        {
            builder.WriteText("Table name is required. Usage: dynamodb describe <table-name>", "error");
            builder.SetExitCode(1);
            return builder.Build();
        }

        var regionOverride = command.Args.TryGetValue("region", out var r) ? r.ToString() : null;
        var client = _credentialManager.GetClient<AmazonDynamoDBClient>(regionOverride);

        try
        {
            var response = await client.DescribeTableAsync(new DescribeTableRequest
            {
                TableName = tableName,
            }, ct);

            var table = response.Table;
            if (table == null)
            {
                builder.WriteText($"Table \"{tableName}\" not found.", "error");
                builder.SetExitCode(1);
                return builder.Build();
            }

            var keySchema = table.KeySchema != null
                ? string.Join(", ", table.KeySchema.Select(k => $"{k.AttributeName} ({k.KeyType})"))
                : "(unknown)";

            builder.WriteKeyValue(new Dictionary<string, string>
            {
                ["TableName"] = table.TableName ?? "(unknown)",
                ["TableStatus"] = table.TableStatus?.Value ?? "(unknown)",
                ["ItemCount"] = (table.ItemCount).ToString(),
                ["TableSizeBytes"] = (table.TableSizeBytes).ToString(),
                ["KeySchema"] = keySchema,
                ["CreationDateTime"] = table.CreationDateTime != default ? table.CreationDateTime.ToString("o") : "(unknown)",
            });
        }
        catch (Exception ex)
        {
            builder.WriteText($"Failed to describe DynamoDB table: {ex.Message}", "error");
            builder.SetExitCode(1);
        }

        return builder.Build();
    }
}

// ---------------------------------------------------------------------------
// dynamodb scan
// ---------------------------------------------------------------------------

internal class DynamoDbScanProcessor : CliCommandProcessor, ICliCommandProcessor
{
    public override string Command { get; set; } = "scan";
    public override string Description { get; set; } = "Scan items from a DynamoDB table";
    public override bool? ValueRequired { get; set; } = true;

    public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } =
    [
        new CliCommandParameterDescriptor
        {
            Name = "limit",
            Aliases = ["-l"],
            Description = "Maximum number of items to return (default: 25)",
            Type = CommandParameterType.Number,
            DefaultValue = "25",
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

    public DynamoDbScanProcessor(AwsCredentialManager credentialManager) => _credentialManager = credentialManager;

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    async Task<ICliStructuredResponse?> ICliCommandProcessor.HandleStructuredAsync(CliProcessCommand command, CancellationToken ct = default)
    {
        var builder = new CliResponseBuilder();
        var tableName = command.Value?.Trim();

        if (string.IsNullOrEmpty(tableName))
        {
            builder.WriteText("Table name is required. Usage: dynamodb scan <table-name>", "error");
            builder.SetExitCode(1);
            return builder.Build();
        }

        var limit = 25;
        if (command.Args.TryGetValue("limit", out var l) && int.TryParse(l.ToString(), out var parsedLimit))
        {
            limit = parsedLimit;
        }

        var regionOverride = command.Args.TryGetValue("region", out var r) ? r.ToString() : null;
        var client = _credentialManager.GetClient<AmazonDynamoDBClient>(regionOverride);

        try
        {
            var response = await client.ScanAsync(new ScanRequest
            {
                TableName = tableName,
                Limit = limit,
            }, ct);

            var items = response.Items ?? [];
            builder.WriteJson(items.Select(SerializeItem).ToArray());
        }
        catch (Exception ex)
        {
            builder.WriteText($"Failed to scan DynamoDB table: {ex.Message}", "error");
            builder.SetExitCode(1);
        }

        return builder.Build();
    }

    internal static Dictionary<string, object?> SerializeItem(Dictionary<string, AttributeValue> item)
    {
        return item.ToDictionary(kvp => kvp.Key, kvp => SerializeAttributeValue(kvp.Value));
    }

    private static object? SerializeAttributeValue(AttributeValue av)
    {
        if (av.S != null) return av.S;
        if (av.N != null) return av.N;
        if (av.BOOL) return av.BOOL;
        if (av.NULL) return null;
        if (av.SS?.Count > 0) return av.SS;
        if (av.NS?.Count > 0) return av.NS;
        if (av.L?.Count > 0) return av.L.Select(SerializeAttributeValue).ToArray();
        if (av.M?.Count > 0) return SerializeItem(av.M);
        return av.S ?? av.N ?? (object?)"(unknown)";
    }
}

// ---------------------------------------------------------------------------
// dynamodb query
// ---------------------------------------------------------------------------

internal class DynamoDbQueryProcessor : CliCommandProcessor, ICliCommandProcessor
{
    public override string Command { get; set; } = "query";
    public override string Description { get; set; } = "Query items from a DynamoDB table";
    public override bool? ValueRequired { get; set; } = true;

    public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } =
    [
        new CliCommandParameterDescriptor
        {
            Name = "key",
            Aliases = ["-k"],
            Description = "KeyConditionExpression for the query",
            Type = CommandParameterType.String,
            Required = true,
        },
        new CliCommandParameterDescriptor
        {
            Name = "filter",
            Aliases = ["-f"],
            Description = "FilterExpression for the query",
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

    public DynamoDbQueryProcessor(AwsCredentialManager credentialManager) => _credentialManager = credentialManager;

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    async Task<ICliStructuredResponse?> ICliCommandProcessor.HandleStructuredAsync(CliProcessCommand command, CancellationToken ct = default)
    {
        var builder = new CliResponseBuilder();
        var tableName = command.Value?.Trim();

        if (string.IsNullOrEmpty(tableName))
        {
            builder.WriteText("Table name is required. Usage: dynamodb query <table-name> --key <expression>", "error");
            builder.SetExitCode(1);
            return builder.Build();
        }

        var keyCondition = command.Args.TryGetValue("key", out var k) ? k.ToString() : null;
        if (string.IsNullOrEmpty(keyCondition))
        {
            builder.WriteText("--key (KeyConditionExpression) is required. Usage: dynamodb query <table-name> --key <expression>", "error");
            builder.SetExitCode(1);
            return builder.Build();
        }

        var filterExpression = command.Args.TryGetValue("filter", out var f) ? f.ToString() : null;
        var regionOverride = command.Args.TryGetValue("region", out var r) ? r.ToString() : null;
        var client = _credentialManager.GetClient<AmazonDynamoDBClient>(regionOverride);

        try
        {
            var request = new QueryRequest
            {
                TableName = tableName,
                KeyConditionExpression = keyCondition,
            };

            if (!string.IsNullOrEmpty(filterExpression))
            {
                request.FilterExpression = filterExpression;
            }

            var response = await client.QueryAsync(request, ct);
            var items = response.Items ?? [];

            builder.WriteJson(items.Select(DynamoDbScanProcessor.SerializeItem).ToArray());
        }
        catch (Exception ex)
        {
            builder.WriteText($"Failed to query DynamoDB table: {ex.Message}", "error");
            builder.SetExitCode(1);
        }

        return builder.Build();
    }
}

// ---------------------------------------------------------------------------
// dynamodb (parent)
// ---------------------------------------------------------------------------

public class DynamoDbProcessor : CliCommandProcessor, ICliCommandProcessor
{
    public override string Command { get; set; } = "dynamodb";
    public override string Description { get; set; } = "AWS DynamoDB operations — tables, describe, scan, query";

    public override IEnumerable<ICliCommandProcessor>? Processors { get; set; }

    public DynamoDbProcessor(AwsCredentialManager credentialManager)
    {
        Processors =
        [
            new DynamoDbTablesProcessor(credentialManager),
            new DynamoDbDescribeProcessor(credentialManager),
            new DynamoDbScanProcessor(credentialManager),
            new DynamoDbQueryProcessor(credentialManager),
        ];
    }

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);
}
