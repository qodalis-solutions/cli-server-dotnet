using Qodalis.Cli.Models;

namespace Qodalis.Cli.Services;

public class CliResponseBuilder : ICliResponseBuilder
{
    private readonly List<CliServerOutput> _outputs = [];
    private int _exitCode;

    public void WriteText(string text, string? style = null)
    {
        _outputs.Add(new TextOutput { Value = text, Style = style });
    }

    public void WriteTable(string[] headers, string[][] rows)
    {
        _outputs.Add(new TableOutput { Headers = headers, Rows = rows });
    }

    public void WriteList(string[] items, bool ordered = false)
    {
        _outputs.Add(new ListOutput { Items = items, Ordered = ordered });
    }

    public void WriteJson(object value)
    {
        _outputs.Add(new JsonOutput { Value = value });
    }

    public void WriteKeyValue(Dictionary<string, string> entries)
    {
        _outputs.Add(new KeyValueOutput
        {
            Entries = entries.Select(e => new KeyValueEntry { Key = e.Key, Value = e.Value }).ToArray()
        });
    }

    public void SetExitCode(int code) => _exitCode = code;

    public CliServerResponse Build() => new()
    {
        ExitCode = _exitCode,
        Outputs = [.. _outputs]
    };
}
