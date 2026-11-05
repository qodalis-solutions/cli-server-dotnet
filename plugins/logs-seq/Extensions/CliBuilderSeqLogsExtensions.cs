using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Qodalis.Cli.Abstractions.Logs;
using Qodalis.Cli.Extensions;

namespace Qodalis.Cli.Plugin.Logs.Seq.Extensions;

/// <summary>
/// Extension methods for the Seq logs plugin on <see cref="CliBuilder"/>.
/// </summary>
public static class CliBuilderSeqLogsExtensions
{
    /// <summary>
    /// Sends the application's logs to Seq, each entry tagged with <see cref="SeqLogsOptions.ServiceName"/>, and (with
    /// <see cref="SeqLogsOptions.ReadHistory"/>) makes Seq the dashboard's log history: persistent, filterable by
    /// service, level, text and time. Without it the history is the admin plugin's in-memory buffer. The live tail is
    /// unchanged. Call it in every service that writes to the same Seq.
    /// </summary>
    /// <param name="builder">The CLI builder instance.</param>
    /// <param name="configure">Sets the Seq server, its API key and the service name.</param>
    /// <returns>The builder for chaining.</returns>
    /// <exception cref="ArgumentException">An option is invalid (see <see cref="SeqLogsOptions.Validate"/>).</exception>
    public static CliBuilder AddSeqLogs(this CliBuilder builder, Action<SeqLogsOptions> configure)
    {
        var options = new SeqLogsOptions();
        configure(options);
        options.Validate();

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(sp => new SeqLogShipper(options));
        builder.Services.AddLogging(logging => logging.Services.AddSingleton<ILoggerProvider>(sp =>
            new SeqLoggerProvider(sp.GetRequiredService<SeqLogShipper>(), options, ServiceName(options, sp))));

        if (options.ReadHistory)
        {
            builder.Services.RemoveAll<ILogStore>();
            builder.Services.AddSingleton<ILogStore>(sp => new SeqLogStore(options, ServiceName(options, sp)));
        }

        builder.AddInstalledPlugin(typeof(SeqLogStore).Assembly);
        return builder;
    }

    private static string ServiceName(SeqLogsOptions options, IServiceProvider services) =>
        string.IsNullOrWhiteSpace(options.ServiceName)
            ? services.GetService<IHostEnvironment>()?.ApplicationName ?? "qodalis-cli"
            : options.ServiceName.Trim();
}
