namespace Qodalis.Cli.Abstractions;

/// <summary>
/// Defines a CLI command processor that handles a specific command and its sub-commands.
/// </summary>
public interface ICliCommandProcessor
{
    /// <summary>
    /// Gets the primary command keyword (e.g., "echo", "hash", "http").
    /// </summary>
    string Command { get; }

    /// <summary>
    /// Gets a human-readable description of what this command does.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Gets the author of this command processor.
    /// </summary>
    ICliCommandAuthor Author { get; }

    /// <summary>
    /// Gets whether this processor accepts commands not listed in <see cref="Processors"/>.
    /// When <c>false</c>, only declared sub-commands are allowed.
    /// </summary>
    bool? AllowUnlistedCommands { get; }

    /// <summary>
    /// Gets whether a value argument is required for this command.
    /// </summary>
    bool? ValueRequired { get; }

    /// <summary>
    /// Gets the version of this command processor.
    /// </summary>
    string Version { get; }

    /// <summary>
    /// Gets the nested sub-command processors (e.g., "encode" and "decode" under "base64").
    /// </summary>
    IEnumerable<ICliCommandProcessor>? Processors { get; }

    /// <summary>
    /// Gets the parameter descriptors accepted by this command.
    /// </summary>
    IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; }

    /// <summary>
    /// Handles the command execution and returns a plain-text result.
    /// </summary>
    /// <param name="command">The parsed command containing the command name, value, and arguments.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The command output as a string.</returns>
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
    /// <param name="command">The parsed command containing the command name, value, and arguments.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A structured response, or <c>null</c> to fall back to <see cref="HandleAsync"/>.</returns>
    Task<ICliStructuredResponse?> HandleStructuredAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<ICliStructuredResponse?>(null);
    }
}

/// <summary>
/// Represents a parsed CLI command with its arguments, value, and chain of sub-commands.
/// </summary>
public class CliProcessCommand
{
    /// <summary>
    /// Gets or sets the primary command keyword.
    /// </summary>
    public string Command { get; set; } = "";

    /// <summary>
    /// Gets or sets optional structured data associated with the command.
    /// </summary>
    public object? Data { get; set; }

    /// <summary>
    /// Gets or sets the chain of sub-command keywords following the primary command.
    /// </summary>
    public IEnumerable<string> ChainCommands { get; set; } = [];

    /// <summary>
    /// Gets or sets the original raw command string as entered by the user.
    /// </summary>
    public string RawCommand { get; set; } = "";

    /// <summary>
    /// Gets or sets the primary value argument of the command.
    /// </summary>
    public string? Value { get; set; } = "";

    /// <summary>
    /// Gets or sets the named arguments parsed from the command input.
    /// </summary>
    public Dictionary<string, object> Args { get; set; } = [];
}