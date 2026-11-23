using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Qodalis.Cli.Models;

namespace Qodalis.Cli.Plugin.Migrations;

/// <summary>
/// The admin dashboard's Migrations page (<c>@qodalis/cli-dashboard-module-migrations</c>), embedded in the package
/// and served at <see cref="Prefix"/>, outside the API, so the dashboard's <c>import()</c> needs no token.
/// </summary>
internal static class MigrationsDashboardPage
{
    public const string Prefix = "/qcli/migrations";

    private static readonly Dictionary<string, string> Titles = new()
    {
        ["es"] = "Migraciones", ["fr"] = "Migrations", ["de"] = "Migrationen", ["pt"] = "Migrações", ["it"] = "Migrazioni",
        ["ja"] = "マイグレーション", ["ko"] = "마이그레이션", ["zh"] = "迁移", ["ru"] = "Миграции", ["ro"] = "Migrări",
    };

    /// <summary>The embedded build, or null when the package was built without it.</summary>
    public static IFileProvider? Files()
    {
        try
        {
            var files = new ManifestEmbeddedFileProvider(typeof(MigrationsDashboardPage).Assembly, "wwwroot/migrations");
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
            Id = "migrations",
            Title = "Migrations",
            Titles = Titles,
            Icon = "🗄️",
            Group = "data",
            Order = 20,
            Permission = MigrationsPermissions.Read,
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
