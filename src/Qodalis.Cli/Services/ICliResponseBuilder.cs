namespace Qodalis.Cli.Services;

public interface ICliResponseBuilder
{
    void WriteText(string text, string? style = null);
    void WriteTable(string[] headers, string[][] rows);
    void WriteList(string[] items, bool ordered = false);
    void WriteJson(object value);
    void WriteKeyValue(Dictionary<string, string> entries);
    void SetExitCode(int code);
    Models.CliServerResponse Build();
}
