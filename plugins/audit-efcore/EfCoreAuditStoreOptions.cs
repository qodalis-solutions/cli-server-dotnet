using Qodalis.Cli.Abstractions.Helpers;

namespace Qodalis.Cli.Plugin.Audit.EfCore;

/// <summary>
/// Options of the EF Core audit store: its table's name (<c>QcliAuditEntries</c> by default; <c>TablePrefix</c>,
/// <c>Tables["AuditEntries"]</c>, <c>Schema</c>) and <c>LegacyTableNames</c> to keep the old <c>audit_entries</c>.
/// </summary>
public class EfCoreAuditStoreOptions : QcliTableNameOptions
{
}
