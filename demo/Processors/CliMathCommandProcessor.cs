using Qodalis.Cli.Abstractions;

namespace Qodalis.Cli.Demo.Processors;

public class CliMathCommandProcessor : CliCommandProcessor
{
    public override string Command { get; set; } = "math";
    public override string Description { get; set; } = "Performs basic math operations";
    public override bool? AllowUnlistedCommands { get; set; } = false;

    public override IEnumerable<ICliCommandProcessor>? Processors { get; set; } = new ICliCommandProcessor[]
    {
        new CliMathAddProcessor(),
        new CliMathMultiplyProcessor(),
    };

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
    {
        return Task.FromResult("Usage: math add|multiply --a <number> --b <number>");
    }
}

public class CliMathAddProcessor : CliCommandProcessor
{
    public override string Command { get; set; } = "add";
    public override string Description { get; set; } = "Adds two numbers";

    public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } =
    [
        new CliCommandParameterDescriptor { Name = "a", Description = "First number", Required = true, Type = CommandParameterType.Number },
        new CliCommandParameterDescriptor { Name = "b", Description = "Second number", Required = true, Type = CommandParameterType.Number },
    ];

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
    {
        if (!command.Args.TryGetValue("a", out var aObj) || !command.Args.TryGetValue("b", out var bObj))
            return Task.FromResult("Error: --a and --b are required");

        if (!double.TryParse(aObj.ToString(), out var a) || !double.TryParse(bObj.ToString(), out var b))
            return Task.FromResult("Error: --a and --b must be numbers");

        return Task.FromResult($"{a} + {b} = {a + b}");
    }
}

public class CliMathMultiplyProcessor : CliCommandProcessor
{
    public override string Command { get; set; } = "multiply";
    public override string Description { get; set; } = "Multiplies two numbers";

    public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } =
    [
        new CliCommandParameterDescriptor { Name = "a", Description = "First number", Required = true, Type = CommandParameterType.Number },
        new CliCommandParameterDescriptor { Name = "b", Description = "Second number", Required = true, Type = CommandParameterType.Number },
    ];

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
    {
        if (!command.Args.TryGetValue("a", out var aObj) || !command.Args.TryGetValue("b", out var bObj))
            return Task.FromResult("Error: --a and --b are required");

        if (!double.TryParse(aObj.ToString(), out var a) || !double.TryParse(bObj.ToString(), out var b))
            return Task.FromResult("Error: --a and --b must be numbers");

        return Task.FromResult($"{a} * {b} = {a * b}");
    }
}
