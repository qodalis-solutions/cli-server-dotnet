namespace Qodalis.Cli.Mcp;

/// <summary>
/// Options of the MCP endpoint (<c>POST /api/qcli/mcp</c>), enabled with <c>CliBuilder.AddMcp()</c>.
/// The endpoint exposes the registered CLI commands as MCP tools to AI clients (Claude Code,
/// Claude Desktop, Cursor, …) with the same permissions as a CLI user.
/// </summary>
public sealed class McpOptions
{
    /// <summary>Enables the endpoint. Defaults to <c>true</c> once the options are given.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Allow-list of tool names (a trailing <c>*</c> matches a prefix, e.g. <c>weather_*</c>).
    /// <c>null</c> or empty = every tool.
    /// </summary>
    public List<string>? Tools { get; set; }

    /// <summary>Deny-list of tool names, same syntax as <see cref="Tools"/>, applied after it.</summary>
    public List<string>? Exclude { get; set; }

    /// <summary>
    /// Static bearer token required by the endpoint when the server has no authentication provider
    /// (e.g. no users plugin). Ignored when the server is protected: sessions and API tokens are used then.
    /// When neither applies the endpoint is as open as <c>/execute</c>.
    /// </summary>
    public string? Token { get; set; }

    /// <summary>Optional instructions returned by <c>initialize</c>.</summary>
    public string? Instructions { get; set; }
}
