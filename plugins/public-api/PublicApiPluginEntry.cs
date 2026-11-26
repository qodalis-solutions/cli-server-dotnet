using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Abstractions.Config;
using Qodalis.Cli.Plugin.Users;
using Qodalis.Cli.Plugins;

[assembly: CliPlugin(typeof(Qodalis.Cli.Plugin.PublicApi.PublicApiPluginEntry), Id = "public-api")]

namespace Qodalis.Cli.Plugin.PublicApi;

/// <summary>
/// The public-api plugin from the dashboard (or <c>cli.AddPlugin&lt;PublicApiPluginEntry&gt;(new { requestsPerDay = 1000 })</c>):
/// the guard of the host's public routes (tokens of the users plugin, per-account quotas), usage counters, and the
/// OpenAPI document (<c>public</c>, added when the host has none) with Swagger UI at <c>basePath</c>. Needs the users
/// plugin (it authenticates the tokens): without it, it is <c>needs-configuration</c>. Settings are read while the
/// server starts.
/// </summary>
public sealed class PublicApiPluginEntry : ICliPluginEntry
{
    /// <summary>Its settings (code options of the same keys are their defaults).</summary>
    public static IReadOnlyList<CliConfigSettingDefinition> Settings =>
    [
        new()
        {
            Key = "basePath", Label = "Path", Description = "Where the OpenAPI document and Swagger UI are served", Type = CliConfigEntryTypes.String,
            Default = "/qcli/api", RequiresRestart = true, Validation = new CliConfigValidation { Required = true, Pattern = "^(/[A-Za-z0-9._~-]+)+$" },
        },
        new() { Key = "title", Label = "Title", Type = CliConfigEntryTypes.String, Default = "API reference", RequiresRestart = true, Validation = new CliConfigValidation { Required = true, Max = 100 } },
        new() { Key = "serverUrl", Label = "Server URL", Description = "The document's servers URL; empty: the request's", Type = CliConfigEntryTypes.String, Default = "", RequiresRestart = true },
        new() { Key = "requestsPerDay", Label = "Requests per day", Description = "Per account; 0: unlimited", Type = CliConfigEntryTypes.Number, Default = 0, RequiresRestart = true, Validation = new CliConfigValidation { Min = 0 } },
        new() { Key = "requestsPerMinute", Label = "Requests per minute", Description = "Per account; 0: unlimited", Type = CliConfigEntryTypes.Number, Default = 0, RequiresRestart = true, Validation = new CliConfigValidation { Min = 0 } },
        new() { Key = "usageRetentionDays", Label = "Usage kept (days)", Type = CliConfigEntryTypes.Number, Default = 31, RequiresRestart = true, Validation = new CliConfigValidation { Min = 1, Max = 3660 } },
    ];

    public void Register(CliPluginContext context)
    {
        var settings = context.BootSettings(new CliPluginSettingsOptions
        {
            Title = "Public API", Description = "The guard, quotas and reference of the host's public API", Settings = [.. Settings],
        });
        if (!context.Services.Any(d => d.ServiceType == typeof(UsersModuleOptions)))
        {
            context.NeedsConfiguration("users plugin");
            return;
        }
        long? Limit(string key) => settings.Number(key, 0) is var n and > 0 ? (long)n : null;
        var quota = new PublicApiQuota(Limit("requestsPerDay"), Limit("requestsPerMinute"));
        var documentName = "public";
        context.Builder.AddPublicApi(o =>
        {
            o.BasePath = settings.Text("basePath") is { Length: > 0 } path ? path : o.BasePath;
            o.Title = settings.Text("title") is { Length: > 0 } title ? title : o.Title;
            o.ServerUrl = settings.Text("serverUrl") is { Length: > 0 } url ? url : null;
            o.UsageRetentionDays = (int)settings.Number("usageRetentionDays", 31);
            o.UseQuotaResolver((_, _, _) => ValueTask.FromResult(quota));
            documentName = o.DocumentName;
        });
        if (!context.Services.Any(d => d.ServiceKey is string key && key == documentName.ToLowerInvariant()
                && d.ServiceType.Name.Contains("OpenApi", StringComparison.Ordinal)))
            context.Services.AddOpenApi(documentName);
        context.Services.AddTransient<IStartupFilter, PublicApiStartupFilter>();
    }

    private sealed class PublicApiStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.UseQodalisPublicApi();
            next(app);
        };
    }
}
