namespace Qodalis.Cli.Services;

/// <summary>
/// Builder for constructing a <see cref="Models.CliServerResponse"/> with multiple output blocks.
/// </summary>
public interface ICliResponseBuilder
{
    /// <summary>Appends a plain-text output block.</summary>
    /// <param name="text">The text content.</param>
    /// <param name="style">Optional style hint (e.g., "error").</param>
    void WriteText(string text, string? style = null);

    /// <summary>Appends a table output block.</summary>
    /// <param name="headers">Column headers.</param>
    /// <param name="rows">Table rows.</param>
    void WriteTable(string[] headers, string[][] rows);

    /// <summary>Appends a list output block.</summary>
    /// <param name="items">The list items.</param>
    /// <param name="ordered">Whether the list is ordered.</param>
    void WriteList(string[] items, bool ordered = false);

    /// <summary>Appends a JSON output block.</summary>
    /// <param name="value">The object to serialize as JSON.</param>
    void WriteJson(object value);

    /// <summary>Appends a key-value pairs output block.</summary>
    /// <param name="entries">The key-value entries.</param>
    void WriteKeyValue(Dictionary<string, string> entries);

    /// <summary>Sets the exit code for the response.</summary>
    /// <param name="code">The exit code (0 for success).</param>
    void SetExitCode(int code);

    /// <summary>Builds and returns the final response.</summary>
    /// <returns>The constructed response.</returns>
    Models.CliServerResponse Build();
}
