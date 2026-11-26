using System.Text.Json.Nodes;
using Qodalis.Cli.Abstractions.Config;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Plugin.Aws.Processors;
using Qodalis.Cli.Plugins;

[assembly: CliPlugin(typeof(Qodalis.Cli.Plugin.Aws.AwsPluginEntry), Id = "aws")]

namespace Qodalis.Cli.Plugin.Aws;

/// <summary>
/// The AWS plugin installed from the dashboard (or <c>cli.AddPlugin&lt;AwsPluginEntry&gt;(new { region = "eu-north-1" })</c>):
/// the <c>aws</c> commands. The region, profile and access keys are settings (the keys secrets: kept server-side, never
/// returned), applied at start and when changed; without keys the AWS default credential chain is used.
/// <c>aws configure set</c> still changes them until the next change or restart.
/// </summary>
public sealed class AwsPluginEntry : ICliPluginEntry
{
    /// <summary>Its settings (the marketplace's <c>config</c>; code options of the same keys are their defaults).</summary>
    public static IReadOnlyList<CliConfigSettingDefinition> Settings(Action<string, JsonNode?>? apply = null) =>
    [
        new() { Key = "region", Label = "Region", Description = "e.g. eu-north-1; empty: the default chain", Type = CliConfigEntryTypes.String, Default = "", Apply = v => apply?.Invoke("region", v) },
        new() { Key = "profile", Label = "Profile", Description = "A profile of the shared AWS config; empty: none", Type = CliConfigEntryTypes.String, Default = "", Apply = v => apply?.Invoke("profile", v) },
        new() { Key = "accessKeyId", Label = "Access key id", Description = "Empty: the AWS default credential chain", Type = CliConfigEntryTypes.Secret, Apply = v => apply?.Invoke("accessKeyId", v) },
        new() { Key = "secretAccessKey", Label = "Secret access key", Type = CliConfigEntryTypes.Secret, Apply = v => apply?.Invoke("secretAccessKey", v) },
    ];

    public void Register(CliPluginContext context)
    {
        var module = new AwsModule();
        var aws = module.Processors.OfType<AwsCommandProcessor>().Single();
        var values = new Dictionary<string, string>();
        void Apply(string key, JsonNode? value)
        {
            lock (values)
            {
                values[key] = value?.ToString().Trim() ?? string.Empty;
                var config = aws.ConfigService;
                if (key == "region" && values[key].Length > 0) config.SetRegion(values[key]);
                if (key == "profile" && values[key].Length > 0) config.SetProfile(values[key]);
                if (values.GetValueOrDefault("accessKeyId") is { Length: > 0 } id && values.GetValueOrDefault("secretAccessKey") is { Length: > 0 } secret)
                    config.SetCredentials(id, secret);
                aws.ClearClients();
            }
        }
        var settings = context.Settings(new CliPluginSettingsOptions
        {
            Title = "AWS", Description = "The aws commands: region and credentials", Settings = [.. Settings(Apply)],
        });
        // The defaults and code options now; stored values when the settings load
        foreach (var key in new[] { "region", "profile", "accessKeyId", "secretAccessKey" }) Apply(key, settings.GetValue("settings", key));
        context.Builder.AddModule(module);
    }
}
