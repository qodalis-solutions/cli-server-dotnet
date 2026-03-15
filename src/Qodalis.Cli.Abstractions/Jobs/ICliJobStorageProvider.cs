namespace Qodalis.Cli.Abstractions.Jobs;

public interface ICliJobStorageProvider
{
    Task SaveExecutionAsync(JobExecution execution, CancellationToken cancellationToken = default);
    Task<(List<JobExecution> Items, int Total)> GetExecutionsAsync(string jobId, int limit = 20, int offset = 0, string? statusFilter = null, CancellationToken cancellationToken = default);
    Task<JobExecution?> GetExecutionAsync(string executionId, CancellationToken cancellationToken = default);
    Task SaveJobStateAsync(string jobId, JobState state, CancellationToken cancellationToken = default);
    Task<JobState?> GetJobStateAsync(string jobId, CancellationToken cancellationToken = default);
    Task<Dictionary<string, JobState>> GetAllJobStatesAsync(CancellationToken cancellationToken = default);
}
