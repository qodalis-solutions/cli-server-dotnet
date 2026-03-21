using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Plugin.Aws.Services;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Plugin.Aws.Processors;

// ---------------------------------------------------------------------------
// aws configure set
// ---------------------------------------------------------------------------

internal class AwsConfigureSetProcessor : CliCommandProcessor, ICliCommandProcessor
{
    public override string Command { get; set; } = "set";
    public override string Description { get; set; } = "Set AWS credentials and region";

    public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } =
    [
        new CliCommandParameterDescriptor
        {
            Name = "key",
            Aliases = ["-k"],
            Description = "AWS access key ID",
            Type = CommandParameterType.String,
        },
        new CliCommandParameterDescriptor
        {
            Name = "secret",
            Aliases = ["-s"],
            Description = "AWS secret access key",
            Type = CommandParameterType.String,
        },
        new CliCommandParameterDescriptor
        {
            Name = "region",
            Aliases = ["-r"],
            Description = "AWS region",
            Type = CommandParameterType.String,
        },
        new CliCommandParameterDescriptor
        {
            Name = "profile",
            Aliases = ["-p"],
            Description = "AWS profile name",
            Type = CommandParameterType.String,
            DefaultValue = "default",
        },
    ];

    private readonly AwsConfigService _configService;
    private readonly AwsCredentialManager _credentialManager;

    public AwsConfigureSetProcessor(AwsConfigService configService, AwsCredentialManager credentialManager)
    {
        _configService = configService;
        _credentialManager = credentialManager;
    }

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    Task<ICliStructuredResponse?> ICliCommandProcessor.HandleStructuredAsync(CliProcessCommand command, CancellationToken ct = default)
    {
        var builder = new CliResponseBuilder();

        var key = command.Args.TryGetValue("key", out var k) ? k.ToString() : null;
        var secret = command.Args.TryGetValue("secret", out var s) ? s.ToString() : null;
        var region = command.Args.TryGetValue("region", out var r) ? r.ToString() : null;
        var profile = command.Args.TryGetValue("profile", out var p) ? p.ToString() : null;

        if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(secret))
        {
            _configService.SetCredentials(key, secret);
            _credentialManager.ClearCache();
        }
        else if (!string.IsNullOrEmpty(key) || !string.IsNullOrEmpty(secret))
        {
            builder.WriteText("Both --key and --secret must be provided together.", "error");
            builder.SetExitCode(1);
            return Task.FromResult<ICliStructuredResponse?>(builder.Build());
        }

        if (!string.IsNullOrEmpty(region))
        {
            _configService.SetRegion(region);
            _credentialManager.ClearCache();
        }

        if (!string.IsNullOrEmpty(profile))
        {
            _configService.SetProfile(profile);
            _credentialManager.ClearCache();
        }

        if (string.IsNullOrEmpty(key) && string.IsNullOrEmpty(secret) && string.IsNullOrEmpty(region) && string.IsNullOrEmpty(profile))
        {
            builder.WriteText("Provide at least one of --key/--secret, --region, or --profile.", "error");
            builder.SetExitCode(1);
            return Task.FromResult<ICliStructuredResponse?>(builder.Build());
        }

        builder.WriteText("AWS configuration updated.", "success");
        return Task.FromResult<ICliStructuredResponse?>(builder.Build());
    }
}

// ---------------------------------------------------------------------------
// aws configure get
// ---------------------------------------------------------------------------

internal class AwsConfigureGetProcessor : CliCommandProcessor, ICliCommandProcessor
{
    public override string Command { get; set; } = "get";
    public override string Description { get; set; } = "Show current AWS configuration (secrets masked)";

    private readonly AwsConfigService _configService;

    public AwsConfigureGetProcessor(AwsConfigService configService) => _configService = configService;

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    Task<ICliStructuredResponse?> ICliCommandProcessor.HandleStructuredAsync(CliProcessCommand command, CancellationToken ct = default)
    {
        var builder = new CliResponseBuilder();
        var summary = _configService.GetConfigSummary();
        builder.WriteKeyValue(summary);
        return Task.FromResult<ICliStructuredResponse?>(builder.Build());
    }
}

// ---------------------------------------------------------------------------
// aws configure profiles
// ---------------------------------------------------------------------------

internal class AwsConfigureProfilesProcessor : CliCommandProcessor, ICliCommandProcessor
{
    public override string Command { get; set; } = "profiles";
    public override string Description { get; set; } = "List available AWS profiles from ~/.aws/credentials and ~/.aws/config";

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    Task<ICliStructuredResponse?> ICliCommandProcessor.HandleStructuredAsync(CliProcessCommand command, CancellationToken ct = default)
    {
        var builder = new CliResponseBuilder();
        var profiles = new SortedSet<string>();

        var credentialsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aws", "credentials");
        var configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aws", "config");

        ParseProfiles(credentialsPath, @"^\[([^\]]+)\]", profiles);
        ParseProfiles(configPath, @"^\[(?:profile\s+)?([^\]]+)\]", profiles);

        if (profiles.Count == 0)
        {
            builder.WriteText("No AWS profiles found in ~/.aws/credentials or ~/.aws/config.", "warning");
            return Task.FromResult<ICliStructuredResponse?>(builder.Build());
        }

        builder.WriteList(profiles.ToArray());
        return Task.FromResult<ICliStructuredResponse?>(builder.Build());
    }

    private static void ParseProfiles(string filePath, string pattern, SortedSet<string> profiles)
    {
        try
        {
            var content = File.ReadAllText(filePath);
            var regex = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.Multiline);
            foreach (System.Text.RegularExpressions.Match match in regex.Matches(content))
            {
                profiles.Add(match.Groups[1].Value.Trim());
            }
        }
        catch
        {
            // File doesn't exist or is unreadable — skip
        }
    }
}

// ---------------------------------------------------------------------------
// aws configure (parent)
// ---------------------------------------------------------------------------

internal class AwsConfigureProcessor : CliCommandProcessor, ICliCommandProcessor
{
    public override string Command { get; set; } = "configure";
    public override string Description { get; set; } = "Manage AWS credentials and configuration";

    public override IEnumerable<ICliCommandProcessor>? Processors { get; set; }

    public AwsConfigureProcessor(AwsConfigService configService, AwsCredentialManager credentialManager)
    {
        Processors =
        [
            new AwsConfigureSetProcessor(configService, credentialManager),
            new AwsConfigureGetProcessor(configService),
            new AwsConfigureProfilesProcessor(),
        ];
    }

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);
}

// ---------------------------------------------------------------------------
// aws status
// ---------------------------------------------------------------------------

internal class AwsStatusProcessor : CliCommandProcessor, ICliCommandProcessor
{
    public override string Command { get; set; } = "status";
    public override string Description { get; set; } = "Test AWS connectivity using STS GetCallerIdentity";

    private readonly AwsCredentialManager _credentialManager;

    public AwsStatusProcessor(AwsCredentialManager credentialManager) => _credentialManager = credentialManager;

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    async Task<ICliStructuredResponse?> ICliCommandProcessor.HandleStructuredAsync(CliProcessCommand command, CancellationToken ct = default)
    {
        var builder = new CliResponseBuilder();

        try
        {
            var client = _credentialManager.GetClient<AmazonSecurityTokenServiceClient>();
            var response = await client.GetCallerIdentityAsync(new GetCallerIdentityRequest(), ct);

            builder.WriteText("AWS connection successful.", "success");
            builder.WriteKeyValue(new Dictionary<string, string>
            {
                ["Account"] = response.Account ?? "(unknown)",
                ["Arn"] = response.Arn ?? "(unknown)",
                ["UserId"] = response.UserId ?? "(unknown)",
            });
        }
        catch (Exception ex) when (
            ex.GetType().Name == "CredentialsProviderError" ||
            ex.Message?.Contains("Could not load credentials") == true ||
            ex.Message?.Contains("No credentials") == true)
        {
            builder.WriteText(
                "No AWS credentials configured. Run \"aws configure set --key <key> --secret <secret>\" or set AWS_ACCESS_KEY_ID / AWS_SECRET_ACCESS_KEY environment variables.",
                "error");
            builder.SetExitCode(1);
        }
        catch (Exception ex)
        {
            builder.WriteText($"AWS status check failed: {ex.Message}", "error");
            builder.SetExitCode(1);
        }

        return builder.Build();
    }
}

// ---------------------------------------------------------------------------
// aws (root processor)
// ---------------------------------------------------------------------------

public class AwsCommandProcessor : CliCommandProcessor, ICliCommandProcessor
{
    public override string Command { get; set; } = "aws";
    public override string Description { get; set; } = "AWS cloud resource management";

    public override IEnumerable<ICliCommandProcessor>? Processors { get; set; }

    private readonly AwsConfigService _configService;
    private readonly AwsCredentialManager _credentialManager;

    public AwsCommandProcessor()
    {
        _configService = new AwsConfigService();
        _credentialManager = new AwsCredentialManager(_configService);

        Processors =
        [
            new AwsConfigureProcessor(_configService, _credentialManager),
            new AwsStatusProcessor(_credentialManager),
            new S3Processor(_credentialManager),
            new Ec2Processor(_credentialManager),
            new LambdaProcessor(_credentialManager),
            new CloudWatchProcessor(_credentialManager),
            new SnsProcessor(_credentialManager),
            new SqsProcessor(_credentialManager),
            new EcsProcessor(_credentialManager),
            new DynamoDbProcessor(_credentialManager),
            new IamProcessor(_credentialManager),
        ];
    }

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken ct = default)
        => Task.FromResult(string.Empty);
}
