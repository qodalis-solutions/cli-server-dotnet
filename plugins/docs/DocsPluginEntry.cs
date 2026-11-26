using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Abstractions.Config;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Plugins;

[assembly: CliPlugin(typeof(Qodalis.Cli.Plugin.Docs.DocsPluginEntry), Id = "docs")]

namespace Qodalis.Cli.Plugin.Docs;

/// <summary>
/// The docs plugin installed from the dashboard (or <c>cli.AddPlugin&lt;DocsPluginEntry&gt;(new { folder = "docs" })</c>):
/// Markdown guides served at <c>basePath</c> (default <c>/qcli/docs</c>), read from a folder (default
/// <c>&lt;data dir&gt;/plugin-data/docs/pages</c>, created) and the <c>docs</c> command. The folder, path, title and
/// name are settings, read when the server starts serving (a restart applies a change).
/// </summary>
public sealed class DocsPluginEntry : ICliPluginEntry
{
    /// <summary>Its settings (the marketplace's <c>config</c>; code options of the same keys are their defaults).</summary>
    public static IReadOnlyList<CliConfigSettingDefinition> Settings(Action<string, JsonNode?>? apply = null) =>
    [
        new()
        {
            Key = "folder", Label = "Folder", Description = "The Markdown files (relative paths from the plugin's data directory)",
            Type = CliConfigEntryTypes.String, Default = "pages", RequiresRestart = true,
            Validation = new CliConfigValidation { Required = true, Max = 500 }, Apply = v => apply?.Invoke("folder", v),
        },
        new()
        {
            Key = "basePath", Label = "Path", Description = "Where the guides are served", Type = CliConfigEntryTypes.String,
            Default = "/qcli/docs", RequiresRestart = true,
            Validation = new CliConfigValidation { Required = true, Pattern = "^(/[A-Za-z0-9._~-]+)+$" }, Apply = v => apply?.Invoke("basePath", v),
        },
        new()
        {
            Key = "title", Label = "Title", Type = CliConfigEntryTypes.String, Default = "Guides", RequiresRestart = true,
            Validation = new CliConfigValidation { Required = true, Max = 100 }, Apply = v => apply?.Invoke("title", v),
        },
        new()
        {
            Key = "name", Label = "Name", Type = CliConfigEntryTypes.String, Default = "Documentation", RequiresRestart = true,
            Validation = new CliConfigValidation { Required = true, Max = 100 }, Apply = v => apply?.Invoke("name", v),
        },
    ];

    public void Register(CliPluginContext context)
    {
        var options = new DocsOptions();
        void Apply(string key, JsonNode? value)
        {
            var text = value?.ToString().Trim() ?? string.Empty;
            switch (key)
            {
                case "folder":
                    var folder = Path.IsPathRooted(text) ? text : Path.Combine(context.DataDir, text.Length > 0 ? text : "pages");
                    Directory.CreateDirectory(folder);
                    options.SetFolder(folder);
                    break;
                case "basePath" when CliBuilderDocsExtensions.IsValidBasePath(text):
                    options.BasePath = text;
                    break;
                case "title" when text.Length > 0:
                    options.Title = text;
                    break;
                case "name" when text.Length > 0:
                    options.Name = text;
                    break;
            }
        }
        var settings = context.Settings(new CliPluginSettingsOptions
        {
            Title = "Docs", Description = "Markdown guides served by the server", Settings = [.. Settings(Apply)],
        });
        foreach (var key in new[] { "folder", "basePath", "title", "name" }) Apply(key, settings.GetValue("settings", key));
        context.Builder.AddDocs(options);
        // Served without the host calling UseQodalisDocs (the stored settings are applied by then)
        context.Services.AddTransient<IStartupFilter, DocsStartupFilter>();
    }

    private sealed class DocsStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.UseQodalisDocs();
            next(app);
        };
    }
}
