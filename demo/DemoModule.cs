using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Demo.Processors;

namespace Qodalis.Cli.Demo;

/// <summary>
/// The demo's own example commands (echo, status, time, hello, math, system, hash, base64) as one module: <c>GET /commands</c> lists them
/// with <c>module: "demo"</c>, and the dashboard's plugins page shows them as the demo plugin (enable/disable).
/// </summary>
public class DemoModule : Cli.CliModule
{
    /// <inheritdoc />
    public override string Name => "demo";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override string Description => "Demo commands: examples of server-side commands";

    /// <inheritdoc />
    public override string? Icon => "🧪";

    /// <inheritdoc />
    public override IEnumerable<ICliCommandProcessor> Processors { get; } =
    [
        new CliEchoCommandProcessor(),
        new CliStatusCommandProcessor(),
        new CliTimeCommandProcessor(),
        new CliHelloCommandProcessor(),
        new CliMathCommandProcessor(),
        new CliSystemCommandProcessor(),
        new CliHashCommandProcessor(),
        new CliBase64CommandProcessor(),
    ];
}
