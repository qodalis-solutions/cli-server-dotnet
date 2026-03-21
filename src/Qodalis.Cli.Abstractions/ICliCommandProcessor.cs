namespace Qodalis.Cli.Abstractions;

public interface ICliCommandProcessor
{
    string Command { get; }

    string Description { get; }

    ICliCommandAuthor Author { get; }

    bool? AllowUnlistedCommands { get; }

    bool? ValueRequired { get; }

    string Version { get; }

    /// <summary>
    /// The API version this processor targets. Default is 1 for backward compatibility.
    /// </summary>
    int ApiVersion { get; }

    IEnumerable<ICliCommandProcessor>? Processors { get; }

    IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; }

    Task<string> HandleAsync
        (
        CliProcessCommand command,
        CancellationToken cancellationToken = default
        );

    /// <summary>
    /// Optional structured response handler. When implemented, the executor
    /// prefers this over HandleAsync for rich output (tables, key-value, etc.).
    /// Return null to fall back to HandleAsync.
    /// </summary>
    Task<ICliStructuredResponse?> HandleStructuredAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<ICliStructuredResponse?>(null);
    }
}

public class CliProcessCommand
{
    public string Command { get; set; } = "";

    public object? Data { get; set; }

    public IEnumerable<string> ChainCommands { get; set; } = [];

    public string RawCommand { get; set; } = "";

    public string? Value { get; set; } = "";

    public Dictionary<string, object> Args { get; set; } = [];
}