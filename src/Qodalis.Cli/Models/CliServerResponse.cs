using Qodalis.Cli.Abstractions;

namespace Qodalis.Cli.Models;

/// <summary>
/// Represents the complete response from a CLI command execution, containing an exit code and output blocks.
/// </summary>
public class CliServerResponse : ICliStructuredResponse
{
    /// <summary>Gets or sets the command exit code (0 for success, non-zero for failure).</summary>
    public int ExitCode { get; set; }

    /// <summary>Gets or sets the list of output blocks produced by the command.</summary>
    public List<CliServerOutput> Outputs { get; set; } = [];
}
