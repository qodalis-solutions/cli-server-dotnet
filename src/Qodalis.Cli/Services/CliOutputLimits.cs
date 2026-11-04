using Qodalis.Cli.Models;

namespace Qodalis.Cli.Services;

/// <summary>
/// Caps unpaged tables at <see cref="Security.CliServerOptions.MaxTableRows"/>.
/// </summary>
public static class CliOutputLimits
{
    /// <summary>
    /// <paramref name="output"/>, or for a table without a page that has more than
    /// <paramref name="maxRows"/> rows, a copy with the first rows and a page marked truncated.
    /// </summary>
    public static CliServerOutput Apply(CliServerOutput output, int maxRows)
    {
        if (maxRows <= 0 || output is not TableOutput { Page: null } table || table.Rows.Length <= maxRows)
            return output;

        return new TableOutput
        {
            Headers = table.Headers,
            Rows = table.Rows[..maxRows],
            Page = new CliTablePage
            {
                Skip = 0,
                Take = maxRows,
                Total = table.Rows.Length,
                HasMore = true,
                Truncated = true,
            },
        };
    }

    /// <summary><paramref name="response"/> with <see cref="Apply"/> applied to every output.</summary>
    public static CliServerResponse Apply(CliServerResponse response, int maxRows)
    {
        if (maxRows <= 0) return response;
        response.Outputs = [.. response.Outputs.Select(o => Apply(o, maxRows))];
        return response;
    }
}
