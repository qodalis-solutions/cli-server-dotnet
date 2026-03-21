namespace Qodalis.Cli.Abstractions;

/// <summary>
/// Marker interface for structured responses. Implemented by CliServerResponse
/// in the Qodalis.Cli project to avoid circular dependency.
/// </summary>
public interface ICliStructuredResponse
{
    int ExitCode { get; set; }
}
