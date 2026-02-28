using Qodalis.Cli.Abstractions;

namespace Qodalis.Cli.Demo.Processors;

public class CliBase64CommandProcessor : CliCommandProcessor
{
    public override string Command { get; set; } = "base64";
    public override string Description { get; set; } = "Encodes or decodes Base64 text";
    public override bool? AllowUnlistedCommands { get; set; } = false;

    public override IEnumerable<ICliCommandProcessor>? Processors { get; set; } = new ICliCommandProcessor[]
    {
        new CliBase64EncodeProcessor(),
        new CliBase64DecodeProcessor(),
    };

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
    {
        return Task.FromResult("Usage: base64 encode|decode <text>");
    }
}

public class CliBase64EncodeProcessor : CliCommandProcessor
{
    public override string Command { get; set; } = "encode";
    public override string Description { get; set; } = "Encodes text to Base64";
    public override bool? ValueRequired { get; set; } = true;

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
    {
        var text = command.Value;
        if (string.IsNullOrWhiteSpace(text))
            return Task.FromResult("Usage: base64 encode <text>");

        return Task.FromResult(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text)));
    }
}

public class CliBase64DecodeProcessor : CliCommandProcessor
{
    public override string Command { get; set; } = "decode";
    public override string Description { get; set; } = "Decodes Base64 to text";
    public override bool? ValueRequired { get; set; } = true;

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
    {
        var text = command.Value;
        if (string.IsNullOrWhiteSpace(text))
            return Task.FromResult("Usage: base64 decode <base64string>");

        try
        {
            return Task.FromResult(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(text)));
        }
        catch
        {
            return Task.FromResult("Error: Invalid Base64 input");
        }
    }
}
