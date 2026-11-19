namespace Qodalis.Cli.Plugin.PublicApi.EfCore;

/// <summary>A row of <c>qcli_api_usage</c>: one token's requests in one window (a UTC day or minute).</summary>
public class ApiUsageEntity
{
    /// <summary>The API token's id.</summary>
    public string TokenId { get; set; } = string.Empty;

    /// <summary>The token owner's id: the account the limits apply to.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary><c>yyyy-MM-dd</c> (a UTC day) or <c>yyyy-MM-ddTHH:mm</c> (a UTC minute).</summary>
    public string Window { get; set; } = string.Empty;

    /// <summary>Requests counted in the window.</summary>
    public long Count { get; set; }
}
