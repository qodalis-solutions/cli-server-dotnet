using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Plugin.Aws.Processors;

namespace Qodalis.Cli.Plugin.Aws;

public class AwsModule : Cli.CliModule
{
    public override string Name => "aws";
    public override string Version => "1.0.0";
    public override string Description => "AWS cloud resource management";

    public override IEnumerable<ICliCommandProcessor> Processors { get; } =
    [
        new AwsCommandProcessor(),
    ];
}
