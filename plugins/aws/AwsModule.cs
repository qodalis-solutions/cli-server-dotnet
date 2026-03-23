using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Plugin.Aws.Processors;

namespace Qodalis.Cli.Plugin.Aws;

/// <summary>
/// CLI module that registers all AWS command processors for cloud resource management.
/// </summary>
public class AwsModule : Cli.CliModule
{
    /// <inheritdoc />
    public override string Name => "aws";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override string Description => "AWS cloud resource management";

    /// <inheritdoc />
    public override IEnumerable<ICliCommandProcessor> Processors { get; } =
    [
        new AwsCommandProcessor(),
    ];
}
