using Qodalis.Cli.Abstractions.Jobs;

namespace Qodalis.Cli.Jobs;

public class JobRegistration
{
    public required string Id { get; set; }
    public required ICliJob Job { get; set; }
    public required CliJobOptions Options { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Active;
    public string? CurrentExecutionId { get; set; }
    public CancellationTokenSource? CurrentCancellation { get; set; }
    public DateTime? NextRunAt { get; set; }
    public DateTime? LastRunAt { get; set; }
    public string? LastRunStatus { get; set; }
    public long? LastRunDuration { get; set; }
    public Timer? Timer { get; set; }
}
