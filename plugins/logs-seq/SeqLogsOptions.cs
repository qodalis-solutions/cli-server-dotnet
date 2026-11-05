using Microsoft.Extensions.Logging;

namespace Qodalis.Cli.Plugin.Logs.Seq;

/// <summary>Where and how <c>cli.AddSeqLogs(...)</c> sends and reads the logs.</summary>
public class SeqLogsOptions
{
    /// <summary>The Seq server, e.g. <c>http://seq:5341</c>. Required.</summary>
    public string ServerUrl { get; set; } = string.Empty;

    /// <summary>
    /// The Seq API key, sent as <c>X-Seq-ApiKey</c>. It needs the Ingest permission, and Read too when
    /// <see cref="ReadHistory"/> is on. Null when the server accepts requests without a key.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// The name every entry carries as the <c>Service</c> property, e.g. <c>ratebell-api</c>, so services that write to
    /// one Seq can be told apart. Defaults to the application name.
    /// </summary>
    public string? ServiceName { get; set; }

    /// <summary>The lowest level sent to Seq (default <see cref="LogLevel.Information"/>); logging filters apply too.</summary>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;

    /// <summary>
    /// Whether the dashboard's Logs page reads its history from Seq (default true). Turn it off in a service that only
    /// writes, such as a worker without the admin plugin.
    /// </summary>
    public bool ReadHistory { get; set; } = true;

    /// <summary>How many entries wait at most while Seq is slow or down; the oldest are dropped beyond it.</summary>
    public int QueueLimit { get; set; } = 10_000;

    /// <summary>The most entries sent in one request.</summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>How long entries wait to be sent together.</summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>The timeout of a request to Seq.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Throws <see cref="ArgumentException"/> when an option is invalid.</summary>
    public void Validate()
    {
        if (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("SeqLogsOptions.ServerUrl must be an absolute http(s) URL.");
        if (QueueLimit < 1) throw new ArgumentException("SeqLogsOptions.QueueLimit must be at least 1.");
        if (BatchSize < 1) throw new ArgumentException("SeqLogsOptions.BatchSize must be at least 1.");
        if (FlushInterval < TimeSpan.Zero) throw new ArgumentException("SeqLogsOptions.FlushInterval may not be negative.");
        if (RequestTimeout <= TimeSpan.Zero) throw new ArgumentException("SeqLogsOptions.RequestTimeout must be positive.");
    }

    /// <summary>The server URL with a trailing slash, for relative request paths.</summary>
    internal Uri BaseAddress => new(ServerUrl.EndsWith('/') ? ServerUrl : ServerUrl + "/");
}
