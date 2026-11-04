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

    /// <summary>
    /// Appends some rows of a longer table. The terminal shows which rows they are under it and the
    /// command for the next ones. Pass <paramref name="total"/> when known; otherwise
    /// <paramref name="hasMore"/> says whether more rows follow (query one row more than
    /// <see cref="Abstractions.Helpers.CliPageRequest.Take"/> to find out).
    /// </summary>
    /// <param name="headers">Column headers.</param>
    /// <param name="rows">The rows returned.</param>
    /// <param name="page">The rows that were asked for (<see cref="Abstractions.Helpers.CliPaging.From"/>).</param>
    /// <param name="total">All rows, when known.</param>
    /// <param name="hasMore">Whether more rows follow; defaults to what <paramref name="total"/> implies.</param>
    void WritePagedTable(string[] headers, string[][] rows, Abstractions.Helpers.CliPageRequest page, long? total = null, bool? hasMore = null);

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
