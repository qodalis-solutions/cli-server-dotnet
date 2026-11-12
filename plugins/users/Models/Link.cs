using System.Text.Json.Serialization;

namespace Qodalis.Cli.Plugin.Users.Models;

/// <summary>Response body of <c>POST /link</c>.</summary>
public class LinkCreated
{
    /// <summary>The terminal's secret handle (32 random bytes, base64url) to poll <c>GET /link/{linkId}</c>.</summary>
    [JsonPropertyName("linkId")]
    public string LinkId { get; set; } = string.Empty;

    /// <summary>The code the user compares on the approval page (<c>ABCD-2345</c>).</summary>
    [JsonPropertyName("userCode")]
    public string UserCode { get; set; } = string.Empty;

    /// <summary>The approval page with the code (root-relative by default).</summary>
    [JsonPropertyName("verificationUrl")]
    public string VerificationUrl { get; set; } = string.Empty;

    /// <summary>Seconds the request waits for its approval.</summary>
    [JsonPropertyName("expiresIn")]
    public int ExpiresIn { get; set; }

    /// <summary>Seconds between two polls.</summary>
    [JsonPropertyName("interval")]
    public int Interval { get; set; }
}

/// <summary>Response body of <c>GET /link/{linkId}</c>: <c>{ status: "pending" | "denied" }</c>.</summary>
public class LinkPoll
{
    [JsonPropertyName("status")]
    [JsonPropertyOrder(-1)]
    public string Status { get; set; } = "pending";
}

/// <summary>An approved request, handed out once: <c>{ status: "approved", token, userName, expiresAt }</c>.</summary>
public class LinkPollApproved : LinkPoll
{
    /// <summary>The API token (<c>qcli_pat_…</c>).</summary>
    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    /// <summary>The approver, whose token it is.</summary>
    [JsonPropertyName("userName")]
    public string UserName { get; set; } = string.Empty;

    /// <summary>When the token expires (Unix ms), null when it never does.</summary>
    [JsonPropertyName("expiresAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public long? ExpiresAt { get; set; }
}

/// <summary>Response body of <c>GET /link/code/{userCode}</c>: the request the approval page shows.</summary>
public class LinkView
{
    [JsonPropertyName("userCode")]
    public string UserCode { get; set; } = string.Empty;

    /// <summary>The terminal's name (<c>Qodalis CLI on Pixel 7</c>).</summary>
    [JsonPropertyName("client")]
    public string Client { get; set; } = string.Empty;

    /// <summary>The <c>Origin</c> header of <c>POST /link</c>, or null.</summary>
    [JsonPropertyName("origin")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Origin { get; set; }

    /// <summary>The terminal's client address, or null.</summary>
    [JsonPropertyName("remoteAddress")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? RemoteAddress { get; set; }

    /// <summary>The requested scope, or null for the approver's permissions.</summary>
    [JsonPropertyName("permissions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public List<string>? Permissions { get; set; }

    [JsonPropertyName("createdAt")]
    public long CreatedAt { get; set; }

    [JsonPropertyName("expiresAt")]
    public long ExpiresAt { get; set; }
}

/// <summary>Response body of <c>POST /link/code/{userCode}/approve</c>.</summary>
public class LinkApproved
{
    [JsonPropertyName("tokenId")]
    public string TokenId { get; set; } = string.Empty;

    [JsonPropertyName("tokenName")]
    public string TokenName { get; set; } = string.Empty;
}
