namespace Qodalis.Cli.Abstractions.Jobs;

public interface ICliJobLogger
{
    void Debug(string message);
    void Info(string message);
    void Warning(string message);
    void Error(string message);
}
