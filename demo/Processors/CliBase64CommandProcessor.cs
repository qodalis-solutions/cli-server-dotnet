using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Abstractions.Helpers;

namespace Qodalis.Cli.Demo.Processors;

public class CliBase64CommandProcessor : CliCommandProcessor
{
    public override string Command { get; set; } = "base64";
    public override string Description { get; set; } = "Encodes or decodes Base64 text";

    public override IEnumerable<string>? Aliases { get; set; } = ["b64"];

    public override IEnumerable<CliCommandExample>? Examples { get; set; } =
    [
        new("base64 encode hello", "Prints aGVsbG8="),
        new("base64 decode aGVsbG8=", "Prints hello"),
    ];

    public override string? Documentation { get; set; } =
        "Encodes text to Base64 or decodes Base64 back to text (UTF-8), on the server.";
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

    public override IEnumerable<string>? Aliases { get; set; } = ["enc"];

    public override IEnumerable<CliCommandExample>? Examples { get; set; } =
    [
        new("base64 encode hello", "Prints aGVsbG8="),
    ];
    public override bool? ValueRequired { get; set; } = true;

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
    {
        var text = CliValueHelpers.GetPositionalText(command);
        if (string.IsNullOrWhiteSpace(text))
            return Task.FromResult("Usage: base64 encode <text>");

        return Task.FromResult(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text)));
    }
}

public class CliBase64DecodeProcessor : CliCommandProcessor
{
    public override string Command { get; set; } = "decode";
    public override string Description { get; set; } = "Decodes Base64 to text";

    public override IEnumerable<string>? Aliases { get; set; } = ["dec"];

    public override IEnumerable<CliCommandExample>? Examples { get; set; } =
    [
        new("base64 decode aGVsbG8=", "Prints hello"),
    ];
    public override bool? ValueRequired { get; set; } = true;

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
    {
        var text = CliValueHelpers.GetPositionalText(command);
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
