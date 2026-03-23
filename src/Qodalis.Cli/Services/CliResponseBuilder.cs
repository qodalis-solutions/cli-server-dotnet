using Qodalis.Cli.Models;

namespace Qodalis.Cli.Services;

/// <summary>
/// Default implementation of <see cref="ICliResponseBuilder"/> that accumulates output blocks and builds a response.
/// </summary>
public class CliResponseBuilder : ICliResponseBuilder
{
    private readonly List<CliServerOutput> _outputs = [];
    private int _exitCode;

    /// <inheritdoc />
    public void WriteText(string text, string? style = null)
    {
        _outputs.Add(new TextOutput { Value = text, Style = style });
    }

    /// <inheritdoc />
    public void WriteTable(string[] headers, string[][] rows)
    {
        _outputs.Add(new TableOutput { Headers = headers, Rows = rows });
    }

    /// <inheritdoc />
    public void WriteList(string[] items, bool ordered = false)
    {
        _outputs.Add(new ListOutput { Items = items, Ordered = ordered });
    }

    /// <inheritdoc />
    public void WriteJson(object value)
    {
        _outputs.Add(new JsonOutput { Value = value });
    }

    /// <inheritdoc />
    public void WriteKeyValue(Dictionary<string, string> entries)
    {
        _outputs.Add(new KeyValueOutput
        {
            Entries = entries.Select(e => new KeyValueEntry { Key = e.Key, Value = e.Value }).ToArray()
        });
    }

    /// <inheritdoc />
    public void SetExitCode(int code) => _exitCode = code;

    /// <inheritdoc />
    public CliServerResponse Build() => new()
    {
        ExitCode = _exitCode,
        Outputs = [.. _outputs]
    };
}
