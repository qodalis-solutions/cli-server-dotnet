namespace Qodalis.Cli.Models;

public class CliServerResponse
{
    public int ExitCode { get; set; }
    public List<CliServerOutput> Outputs { get; set; } = [];
}
