using Qodalis.Cli.Abstractions;

namespace Qodalis.Cli.Models;

public class CliServerResponse : ICliStructuredResponse
{
    public int ExitCode { get; set; }
    public List<CliServerOutput> Outputs { get; set; } = [];
}
