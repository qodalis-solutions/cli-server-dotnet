using System.Runtime.Loader;
using Microsoft.Extensions.DependencyInjection;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Abstractions.Config;
using Qodalis.Cli.Plugins;

[assembly: CliPlugin(typeof(Qodalis.Cli.Plugin.TestFixture.FixturePluginEntry), Id = "fixture", Order = CliPluginOrder.Source)]

namespace Qodalis.Cli.Plugin.TestFixture;

/// <summary>Seen by the tests in the server's services (a type of the plugin's own assembly).</summary>
public sealed class FixtureMarker(string source, string dataDir)
{
    public string Source { get; } = source;
    public string DataDir { get; } = dataDir;
}

/// <summary>
/// Registers <c>fixture-hello</c> and its settings (<c>greeting</c>, default Hello; <c>apiKey</c>, a required secret).
/// A file <c>fail</c> in its data directory makes it throw after adding a service (the loader must take it back).
/// </summary>
public sealed class FixturePluginEntry : ICliPluginEntry
{
    public void Register(CliPluginContext context)
    {
        var settings = context.Settings(new CliPluginSettingsOptions
        {
            Title = "Fixture",
            Settings =
            [
                new CliConfigSettingDefinition { Key = "greeting", Type = CliConfigEntryTypes.String, Default = "Hello" },
                new CliConfigSettingDefinition { Key = "apiKey", Type = CliConfigEntryTypes.Secret, Required = true },
            ],
        });
        context.Services.AddSingleton(new FixtureMarker(context.Source, context.DataDir));
        if (File.Exists(Path.Combine(context.DataDir, "fail"))) throw new InvalidOperationException("fixture asked to fail");
        context.Logger.LogFixture();
        context.Builder.AddProcessor(new FixtureProcessor(() => settings.GetValue("settings", "greeting")?.ToString() ?? "?"));
    }
}

internal static class FixtureLog
{
    public static void LogFixture(this Microsoft.Extensions.Logging.ILogger logger) =>
        Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(logger, "fixture registered");
}

public sealed class FixtureProcessor(Func<string> greeting) : CliCommandProcessor
{
    public override string Command { get; set; } = "fixture-hello";
    public override string Description { get; set; } = "Says hello from the fixture plugin";

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default) =>
        Task.FromResult($"{greeting()} from {AssemblyLoadContext.GetLoadContext(GetType().Assembly)?.Name}");
}
