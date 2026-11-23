using Qodalis.Cli.Models;

namespace Qodalis.Cli.Plugin.Databases;

/// <summary>
/// The admin dashboard's Databases page (<c>@qodalis/cli-dashboard-module-databases</c>), embedded in the package
/// and served at <c>/qcli/databases</c> by <c>CliBuilder.AddDashboardModule</c>; it also shows cards on the Overview.
/// </summary>
internal static class DatabasesDashboardPage
{
    public static readonly CliDashboardModule Module = new()
    {
        Id = "databases",
        Title = "Databases",
        Titles = new Dictionary<string, string>
        {
            ["es"] = "Bases de datos",
            ["fr"] = "Bases de données",
            ["de"] = "Datenbanken",
            ["pt"] = "Bancos de dados",
            ["it"] = "Database",
            ["ja"] = "データベース",
            ["ko"] = "데이터베이스",
            ["zh"] = "数据库",
            ["ru"] = "Базы данных",
            ["ro"] = "Baze de date",
        },
        Icon = "🗄️",
        Group = "data",
        Order = 20,
        Permission = DatabasesPermissions.Read,
        Widgets = ["overview"],
        Prefix = "/qcli/databases",
    };
}
