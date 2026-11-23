using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Qodalis.Cli.Models;

namespace Qodalis.Cli.Plugin.Databases;

/// <summary>
/// The admin dashboard's Databases page (<c>@qodalis/cli-dashboard-module-databases</c>), embedded in the package
/// and served at <see cref="Prefix"/>, outside the API, so the dashboard's <c>import()</c> needs no token.
/// </summary>
internal static class DatabasesDashboardPage
{
    public const string Prefix = "/qcli/databases";

    private static readonly Dictionary<string, string> Titles = new()
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
    };

    /// <summary>The embedded build, or null when the package was built without it.</summary>
    public static IFileProvider? Files()
    {
        try
        {
            var files = new ManifestEmbeddedFileProvider(typeof(DatabasesDashboardPage).Assembly, "wwwroot/databases");
            return files.GetFileInfo("extension.js").Exists ? files : null;
        }
        catch (InvalidOperationException)
        {
            // No embedded files at all
            return null;
        }
    }

    /// <summary>The page's dashboard extension; the URLs carry the files' hash (<c>?v=</c>), so a new build loads at once.</summary>
    public static CliDashboardExtension Extension(IFileProvider files)
    {
        string? Version(string file)
        {
            var info = files.GetFileInfo(file);
            if (!info.Exists) return null;
            using var stream = info.CreateReadStream();
            return Convert.ToHexStringLower(SHA256.HashData(stream))[..12];
        }

        var css = Version("extension.css");
        return new CliDashboardExtension
        {
            Id = "databases",
            Title = "Databases",
            Titles = Titles,
            Icon = "🗄️",
            Group = "data",
            Order = 20,
            Permission = DatabasesPermissions.Read,
            Entry = $"{Prefix}/extension.js?v={Version("extension.js")}",
            Styles = css == null ? null : [$"{Prefix}/extension.css?v={css}"],
        };
    }

    /// <summary>Serves the files at <see cref="Prefix"/> before the rest of the pipeline.</summary>
    internal sealed class StartupFilter(IFileProvider files) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = files,
                RequestPath = Prefix,
                OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache",
            });
            next(app);
        };
    }
}
