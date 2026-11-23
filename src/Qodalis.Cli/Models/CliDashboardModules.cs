using Qodalis.Cli.Security;

namespace Qodalis.Cli.Models;

/// <summary>
/// The dashboard pages of the built-in plugins, each served by the package that owns its API (it embeds the build and
/// calls <c>CliBuilder.AddDashboardModule</c>): jobs by the jobs plugin, audit by the audit plugin, users, account and
/// API tokens by the users plugin, filesystem and data explorer by this package (<c>AddFileSystem</c>, <c>AddDataExplorerProvider</c>).
/// </summary>
public static class CliDashboardModules
{
    /// <summary>Jobs: the page, its Overview cards, palette entries (jump to a job) and the palette's job search.</summary>
    public static readonly CliDashboardModule Jobs = new()
    {
        Id = "jobs",
        Title = "Jobs",
        Titles = Translations("Tareas", "Tâches", "Jobs", "Tarefas", "Job", "ジョブ", "작업", "任务", "Задания", "Joburi"),
        Icon = "⏱",
        Group = "operations",
        Order = 10,
        Permission = CliRoutePolicy.JobsRead,
        Widgets = ["overview", "palette"],
        Search = "/?q=",
    };

    /// <summary>The audit log, with the palette's audit search.</summary>
    public static readonly CliDashboardModule Audit = new()
    {
        Id = "audit",
        Title = "Audit",
        Titles = Translations("Auditoría", "Audit", "Audit", "Auditoria", "Audit", "監査", "감사", "审计", "Аудит", "Audit"),
        Icon = "🛡",
        Group = "operations",
        Order = 30,
        Permission = CliRoutePolicy.AuditRead,
        Search = "/?q=",
    };

    /// <summary>The filesystem API's files.</summary>
    public static readonly CliDashboardModule FileSystem = new()
    {
        Id = "filesystem",
        Title = "Filesystem",
        Titles = Translations("Archivos", "Fichiers", "Dateien", "Arquivos", "File", "ファイル", "파일", "文件", "Файлы", "Fișiere"),
        Icon = "📁",
        Group = "data",
        Order = 10,
        Permission = CliRoutePolicy.FilesRead,
    };

    /// <summary>The data explorer's sources and queries.</summary>
    public static readonly CliDashboardModule DataExplorer = new()
    {
        Id = "data-explorer",
        Title = "Data explorer",
        Titles = Translations(
            "Explorador de datos", "Explorateur de données", "Daten-Explorer", "Explorador de dados", "Esplora dati",
            "データエクスプローラー", "데이터 탐색기", "数据浏览器", "Обозреватель данных", "Explorator de date"),
        Icon = "⛁",
        Group = "data",
        Order = 15,
        Permission = CliRoutePolicy.DataQuery,
    };

    /// <summary>API tokens: every user sees their own, so no permission.</summary>
    public static readonly CliDashboardModule Tokens = new()
    {
        Id = "tokens",
        Title = "API tokens",
        Titles = Translations("Tokens de API", "Jetons d’API", "API-Tokens", "Tokens de API", "Token API", "API トークン", "API 토큰", "API 令牌", "API-токены", "Tokenuri API"),
        Icon = "🔑",
        Group = "access",
        Order = 10,
    };

    /// <summary>Users, groups and sessions, with the palette's user search.</summary>
    public static readonly CliDashboardModule Users = new()
    {
        Id = "users",
        Title = "Users",
        Titles = Translations("Usuarios", "Utilisateurs", "Benutzer", "Usuários", "Utenti", "ユーザー", "사용자", "用户", "Пользователи", "Utilizatori"),
        Icon = "👥",
        Group = "access",
        Order = 5,
        Permission = "users:read",
        Search = "/?q=",
    };

    /// <summary>
    /// The signed-in user's account (profile, password change): every user, in the dashboard's account menu. The
    /// <see cref="Users"/> build renders it for this id; served again at <c>/qcli/modules/account</c>.
    /// </summary>
    public static readonly CliDashboardModule Account = new()
    {
        Id = "account",
        Title = "Account",
        Titles = Translations("Cuenta", "Compte", "Konto", "Conta", "Account", "アカウント", "계정", "账户", "Учётная запись", "Cont"),
        Icon = "👤",
        Group = "account",
        Order = 0,
        Build = "users",
    };

    private static Dictionary<string, string> Translations(string es, string fr, string de, string pt, string it, string ja, string ko, string zh, string ru, string ro) => new()
    {
        ["es"] = es,
        ["fr"] = fr,
        ["de"] = de,
        ["pt"] = pt,
        ["it"] = it,
        ["ja"] = ja,
        ["ko"] = ko,
        ["zh"] = zh,
        ["ru"] = ru,
        ["ro"] = ro,
    };
}
