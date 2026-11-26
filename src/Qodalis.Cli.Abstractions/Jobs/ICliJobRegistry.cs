namespace Qodalis.Cli.Abstractions.Jobs;

/// <summary>
/// Registers jobs with the jobs plugin's scheduler at runtime, for plugins that schedule their own work without
/// referencing the jobs plugin (the admin plugin's update check). In the container when the jobs plugin is mounted.
/// A job registered after the scheduler started is scheduled at once.
/// </summary>
public interface ICliJobRegistry
{
    /// <summary>True when a job with this id is registered.</summary>
    bool IsRegistered(string id);

    /// <summary>
    /// Registers <paramref name="job"/> with <paramref name="options"/> and returns its id. Throws
    /// <see cref="ArgumentException"/> for invalid options and <see cref="InvalidOperationException"/> when the id is taken.
    /// </summary>
    string Register(ICliJob job, CliJobOptions options);
}
