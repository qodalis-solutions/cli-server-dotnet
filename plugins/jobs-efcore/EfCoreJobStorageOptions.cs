using Qodalis.Cli.Abstractions.Helpers;

namespace Qodalis.Cli.Plugin.Jobs.EfCore;

/// <summary>
/// Options of the EF Core job storage: its tables' names (<c>QcliJobExecutions</c>, <c>QcliJobLogEntries</c>,
/// <c>QcliJobStates</c> by default; <c>TablePrefix</c>, <c>Tables["JobStates"]</c>, <c>Schema</c>) and
/// <c>LegacyTableNames</c> to keep the old <c>job_*</c> names.
/// </summary>
public class EfCoreJobStorageOptions : QcliTableNameOptions
{
}
