using System.Text.Json.Nodes;
using Qodalis.Cli.Abstractions.Config;
using Qodalis.Cli.Abstractions.Jobs;

namespace Qodalis.Cli.Plugin.Jobs;

/// <summary>
/// <c>jobs</c>: the history retention every job gets (unless it sets its own), changeable at runtime; the next
/// <c>jobs:prune-history</c> run applies it. The prune interval is set in code.
/// </summary>
public static class JobsConfigProvider
{
    public static CliStoredConfigProvider Create(CliJobScheduler scheduler)
    {
        var initial = scheduler.History;
        JsonNode? AsDuration(TimeSpan? value) => value is { } v ? CliConfigValues.FormatDuration(v) : null;
        JsonNode? AsCount(int? value) => value is { } v ? v : null;

        void Apply(Action<JobHistoryRetention, JsonNode?> set, JsonNode? value)
        {
            var current = scheduler.History;
            var limits = new JobHistoryRetention
            {
                MaxAge = current.MaxAge,
                MaxRunsPerJob = current.MaxRunsPerJob,
                FailedMaxAge = current.FailedMaxAge,
                FailedMaxRunsPerJob = current.FailedMaxRunsPerJob,
            };
            set(limits, value);
            scheduler.SetHistoryDefaults(limits);
        }

        static TimeSpan? Duration(JsonNode? value) =>
            CliConfigValues.ParseDuration(value) is { } ms ? TimeSpan.FromMilliseconds(ms) : null;
        static int? Count(JsonNode? value) => CliConfigValues.AsNumber(value) is { } n ? (int)n : null;
        static string? CountRule(JsonNode value) => CliConfigValues.IsInteger(value) ? null : "must be a whole number";

        return new CliStoredConfigProvider(new CliStoredConfigProviderOptions
        {
            Id = "jobs",
            Title = "Jobs",
            Description = "Defaults of the background jobs",
            Order = 60,
            Sections =
            [
                new CliConfigSectionDefinition
                {
                    Id = "history",
                    Title = "Run history retention",
                    Description = "Applied to every job without its own history options by the jobs:prune-history job; empty keeps every run",
                    Settings =
                    [
                        new CliConfigSettingDefinition
                        {
                            Key = "maxAge", Label = "Keep runs for", Description = "Delete runs that started longer ago than this",
                            Type = CliConfigEntryTypes.Duration, Default = AsDuration(initial.MaxAge),
                            Validation = new CliConfigValidation { Min = 1000 },
                            Apply = value => Apply((l, v) => l.MaxAge = Duration(v), value),
                        },
                        new CliConfigSettingDefinition
                        {
                            Key = "maxRunsPerJob", Label = "Runs kept per job", Description = "Keep at most this many runs per job, the newest",
                            Type = CliConfigEntryTypes.Number, Default = AsCount(initial.MaxRunsPerJob),
                            Validation = new CliConfigValidation { Min = 1 },
                            Validate = CountRule,
                            Apply = value => Apply((l, v) => l.MaxRunsPerJob = Count(v), value),
                        },
                        new CliConfigSettingDefinition
                        {
                            Key = "failedMaxAge", Label = "Keep failed runs for", Description = "Delete failed runs that started longer ago than this (default: Keep runs for)",
                            Type = CliConfigEntryTypes.Duration, Default = AsDuration(initial.FailedMaxAge),
                            Validation = new CliConfigValidation { Min = 1000 },
                            Apply = value => Apply((l, v) => l.FailedMaxAge = Duration(v), value),
                        },
                        new CliConfigSettingDefinition
                        {
                            Key = "failedMaxRunsPerJob", Label = "Failed runs kept per job", Description = "Keep at most this many failed runs per job, counted on their own",
                            Type = CliConfigEntryTypes.Number, Default = AsCount(initial.FailedMaxRunsPerJob),
                            Validation = new CliConfigValidation { Min = 1 },
                            Validate = CountRule,
                            Apply = value => Apply((l, v) => l.FailedMaxRunsPerJob = Count(v), value),
                        },
                        new CliConfigSettingDefinition
                        {
                            Key = "pruneInterval", Label = "Prune every", Description = "How often jobs:prune-history runs (set in code)",
                            Type = CliConfigEntryTypes.Duration, Mutable = false, RequiresRestart = true,
                            Default = CliConfigValues.FormatDuration(initial.PruneInterval),
                        },
                    ],
                },
            ],
        });
    }
}
