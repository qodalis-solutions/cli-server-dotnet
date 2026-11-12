using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Abstractions.Audit;
using Qodalis.Cli.Abstractions.Insights;
using Qodalis.Cli.Abstractions.Jobs;
using Qodalis.Cli.Abstractions.Logs;
using Qodalis.Cli.Controllers;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Plugin.Admin.Controllers;
using Qodalis.Cli.Plugin.Admin.Extensions;
using Qodalis.Cli.Plugin.Admin.Services;
using Qodalis.Cli.Plugin.Audit;
using Qodalis.Cli.Plugin.Audit.Controllers;
using Qodalis.Cli.Plugin.Audit.EfCore;
using Qodalis.Cli.Plugin.Jobs;
using Qodalis.Cli.Plugin.Jobs.EfCore;
using Qodalis.Cli.Plugin.Logs.Seq;
using Qodalis.Cli.Services;
using Qodalis.Cli.Tests.Helpers;

namespace Qodalis.Cli.Tests;

internal static class Insights
{
    public static readonly DateTime Since = new(2026, 11, 4, 0, 0, 0, DateTimeKind.Utc);
    public static readonly DateTime Until = Since.AddDays(1);
    public const string SinceText = "2026-11-04T00:00:00Z";
    public const string UntilText = "2026-11-05T00:00:00Z";

    public static JsonElement Json(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return JsonSerializer.SerializeToElement(ok.Value);
    }

    public static JsonElement Json(object? value) => JsonSerializer.SerializeToElement(value);

    public static string Error(IActionResult result, int status = 400)
    {
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(status, objectResult.StatusCode);
        return Json(objectResult.Value).GetProperty("error").GetString()!;
    }
}

/// <summary>A host's job manager over a store: what RateBell-like setups look like.</summary>
internal sealed class StoreJobManager(ICliJobStorageProvider store, params CliJobInfo[] jobs) : ICliJobManager
{
    public int Queries;

    public Task<IReadOnlyList<CliJobInfo>> GetJobsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CliJobInfo>>(jobs);

    public Task TriggerAsync(string jobId, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task PauseAsync(string jobId, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task ResumeAsync(string jobId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public async Task<(IReadOnlyList<JobExecution> Items, int Total)> GetExecutionsAsync(
        string jobId, int limit, int offset, string? status, CancellationToken cancellationToken) =>
        await store.GetExecutionsAsync(jobId, limit, offset, status, cancellationToken);

    public Task<(IReadOnlyList<JobExecution> Items, int Total)> QueryExecutionsAsync(
        JobExecutionQuery query, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Queries);
        return store.QueryExecutionsAsync(query, cancellationToken);
    }

    public Task<JobExecution?> GetExecutionAsync(string jobId, string executionId, CancellationToken cancellationToken) =>
        store.GetExecutionAsync(executionId, cancellationToken);
}

/// <summary>A store that returns at most 100 executions a page, whatever is asked.</summary>
internal sealed class ClampingJobStore : ICliJobStorageProvider
{
    private readonly InMemoryJobStorageProvider _inner = new();

    public Task SaveExecutionAsync(JobExecution execution, CancellationToken cancellationToken = default) =>
        _inner.SaveExecutionAsync(execution, cancellationToken);

    public Task<(List<JobExecution> Items, int Total)> GetExecutionsAsync(string jobId, int limit = 20, int offset = 0,
        string? statusFilter = null, CancellationToken cancellationToken = default) =>
        _inner.GetExecutionsAsync(jobId, Math.Min(limit, 100), offset, statusFilter, cancellationToken);

    public Task<(IReadOnlyList<JobExecution> Items, int Total)> QueryExecutionsAsync(JobExecutionQuery query,
        CancellationToken cancellationToken = default)
    {
        query.Limit = Math.Min(query.Limit, 100);
        return _inner.QueryExecutionsAsync(query, cancellationToken);
    }

    public Task<JobExecution?> GetExecutionAsync(string executionId, CancellationToken cancellationToken = default) =>
        _inner.GetExecutionAsync(executionId, cancellationToken);

    public Task SaveJobStateAsync(string jobId, JobState state, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<JobState?> GetJobStateAsync(string jobId, CancellationToken cancellationToken = default) => Task.FromResult<JobState?>(null);
    public Task<Dictionary<string, JobState>> GetAllJobStatesAsync(CancellationToken cancellationToken = default) => Task.FromResult(new Dictionary<string, JobState>());
}

public class JobStatsTests
{
    private static readonly CliJobInfo JobA = new() { Id = "a", Name = "Job A", Group = "Rates" };
    private static readonly CliJobInfo JobB = new() { Id = "b", Name = "Job B", Group = "rates" };
    private static readonly CliJobInfo JobC = new() { Id = "c", Name = "Job C", Group = "other" };

    private static JobExecution Run(string job, JobExecutionStatus status, DateTime startedAt, long? duration, string? id = null) => new()
    {
        Id = id ?? Guid.NewGuid().ToString("N")[..12],
        JobId = job,
        JobName = "Job " + job.ToUpperInvariant(),
        Status = status,
        StartedAt = startedAt,
        Duration = duration,
    };

    private static async Task<CliJobsController> ControllerAsync(ICliJobStorageProvider store, params JobExecution[] runs)
    {
        foreach (var run in runs) await store.SaveExecutionAsync(run);
        return new CliJobsController(new StoreJobManager(store, JobA, JobB, JobC));
    }

    private static JobExecution[] SampleRuns()
    {
        var s = Insights.Since;
        return
        [
            Run("a", JobExecutionStatus.Completed, s.AddMinutes(30), 1000),
            Run("a", JobExecutionStatus.Failed, s.AddMinutes(70), 3000),
            Run("a", JobExecutionStatus.TimedOut, s.AddMinutes(80), 5000),
            Run("a", JobExecutionStatus.Cancelled, s.AddHours(5), 200),
            Run("a", JobExecutionStatus.Running, s.AddHours(23).AddMinutes(59), null),
            Run("b", JobExecutionStatus.Completed, s.AddHours(2), 100),
            Run("b", JobExecutionStatus.Failed, s.AddMinutes(-1), 9999), // before the range
            Run("c", JobExecutionStatus.Failed, Insights.Until, 9999), // the end is exclusive
        ];
    }

    private static Task<IActionResult> Stats(CliJobsController c, string? buckets = null, string[]? job = null, string? group = null,
        string? since = Insights.SinceText, string? until = Insights.UntilText) =>
        c.GetStats(since, until, buckets, job, group, ct: CancellationToken.None);

    [Fact]
    public async Task Stats_CountsOutcomesPerColumn_TotalsAndJobs()
    {
        var c = await ControllerAsync(new InMemoryJobStorageProvider(), SampleRuns());

        var json = Insights.Json(await Stats(c));

        Assert.Equal("2026-11-04T00:00:00.000Z", json.GetProperty("since").GetString());
        Assert.Equal("2026-11-05T00:00:00.000Z", json.GetProperty("until").GetString());
        Assert.Equal(3_600_000, json.GetProperty("bucketMs").GetDouble());
        var buckets = json.GetProperty("buckets").EnumerateArray().ToList();
        Assert.Equal(24, buckets.Count);
        Assert.Equal("2026-11-04T01:00:00.000Z", buckets[1].GetProperty("start").GetString());
        Assert.Equal(1, buckets[0].GetProperty("succeeded").GetInt32());
        Assert.Equal(2, buckets[1].GetProperty("failed").GetInt32());
        Assert.Equal(1, buckets[2].GetProperty("succeeded").GetInt32());
        Assert.Equal(1, buckets[5].GetProperty("cancelled").GetInt32());
        Assert.Equal(1, buckets[23].GetProperty("running").GetInt32());

        var totals = json.GetProperty("totals");
        Assert.Equal(6, totals.GetProperty("total").GetInt32());
        Assert.Equal(2, totals.GetProperty("succeeded").GetInt32());
        Assert.Equal(2, totals.GetProperty("failed").GetInt32());
        Assert.Equal(1, totals.GetProperty("cancelled").GetInt32());
        Assert.Equal(1, totals.GetProperty("running").GetInt32());
        Assert.Equal(0.5, totals.GetProperty("successRate").GetDouble());
        // Finished runs with a duration: 100, 200, 1000, 3000, 5000
        Assert.Equal(1860, totals.GetProperty("avgDuration").GetInt64());
        Assert.Equal(5000, totals.GetProperty("p95Duration").GetInt64());
        Assert.Equal(5000, totals.GetProperty("maxDuration").GetInt64());
        Assert.Equal("2026-11-04T01:20:00.000Z", totals.GetProperty("lastFailureAt").GetString());

        var jobs = json.GetProperty("jobs").EnumerateArray().ToList();
        Assert.Equal(["a", "b"], jobs.Select(j => j.GetProperty("jobId").GetString()));
        var a = jobs[0];
        Assert.Equal("Job A", a.GetProperty("jobName").GetString());
        Assert.Equal(5, a.GetProperty("total").GetInt32());
        Assert.Equal(0.333, a.GetProperty("successRate").GetDouble());
        Assert.Equal(5000, a.GetProperty("maxDuration").GetInt64());
        var recent = a.GetProperty("recent").EnumerateArray().ToList();
        Assert.Equal(5, recent.Count);
        Assert.Equal("running", recent[0].GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, recent[0].GetProperty("duration").ValueKind);
        Assert.Equal("timedout", recent[2].GetProperty("status").GetString());
        Assert.Equal("2026-11-04T00:30:00.000Z", recent[4].GetProperty("startedAt").GetString());
        var b = jobs[1];
        Assert.Equal(1.0, b.GetProperty("successRate").GetDouble());
        Assert.Equal(JsonValueKind.Null, b.GetProperty("lastFailureAt").ValueKind);
    }

    [Theory]
    [InlineData("completed", "succeeded")]
    [InlineData("SUCCEEDED", "succeeded")]
    [InlineData("failed", "failed")]
    [InlineData("error", "failed")]
    [InlineData("timed_out", "failed")]
    [InlineData("TimedOut", "failed")]
    [InlineData("running", "running")]
    [InlineData("pending", "running")]
    [InlineData("cancelled", "cancelled")]
    [InlineData("skipped", "cancelled")]
    [InlineData(null, "cancelled")]
    public void Outcome_MapsTheStatus(string? status, string outcome) => Assert.Equal(outcome, JobStatistics.Outcome(status));

    [Fact]
    public void Percentile_IsTheNearestRank()
    {
        Assert.Equal(19, TimeBuckets.Percentile(Enumerable.Range(1, 20).Select(i => (long)i).ToList(), 0.95));
        Assert.Equal(10, TimeBuckets.Percentile(Enumerable.Range(1, 10).Select(i => (long)i).ToList(), 0.95));
        Assert.Equal(7, TimeBuckets.Percentile([7], 0.95));
        Assert.Null(TimeBuckets.Percentile([], 0.95));
    }

    [Fact]
    public async Task Stats_ReadsEveryRun_PagingPastAStoreThatCapsItsPages()
    {
        var store = new ClampingJobStore();
        var runs = Enumerable.Range(0, 1234)
            .Select(i => Run(i % 2 == 0 ? "a" : "b", JobExecutionStatus.Completed, Insights.Since.AddSeconds(i * 60), 10))
            .ToArray();
        foreach (var run in runs) await store.SaveExecutionAsync(run);
        var manager = new StoreJobManager(store, JobA, JobB);

        var json = Insights.Json(await Stats(new CliJobsController(manager)));

        Assert.Equal(1234, json.GetProperty("totals").GetProperty("total").GetInt32());
        Assert.Equal(1234, json.GetProperty("buckets").EnumerateArray().Sum(b => b.GetProperty("succeeded").GetInt32()));
        Assert.True(manager.Queries >= 13);
        Assert.Equal(12, json.GetProperty("jobs")[0].GetProperty("recent").GetArrayLength());
    }

    [Fact]
    public async Task Stats_WorksWithTheEfCoreStore()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<JobStorageDbContext>().UseSqlite(connection).Options;
        var store = new EfCoreJobStorageProvider(new SqliteContextFactory<JobStorageDbContext>(options, o => new JobStorageDbContext(o)));

        var json = Insights.Json(await Stats(await ControllerAsync(store, SampleRuns())));

        Assert.Equal(6, json.GetProperty("totals").GetProperty("total").GetInt32());
        Assert.Equal(5000, json.GetProperty("totals").GetProperty("p95Duration").GetInt64());
    }

    [Fact]
    public async Task Stats_WorksWithAManagerThatOnlyListsPerJob()
    {
        // FakeJobManager has no query of its own: the default merges GetExecutionsAsync per job. Its run starts "now".
        var until = TimeBuckets.Format(DateTime.UtcNow.AddMinutes(1));
        var json = Insights.Json(await new CliJobsController(new FakeJobManager())
            .GetStats(null, until, null, null, null, ct: CancellationToken.None));

        Assert.Equal(1, json.GetProperty("totals").GetProperty("total").GetInt32());
        Assert.Equal("fetch-rates:bnr", json.GetProperty("jobs")[0].GetProperty("jobId").GetString());
    }

    [Fact]
    public async Task Stats_FiltersByJobAndGroup_AndAGroupWithoutJobsIsEmpty()
    {
        var c = await ControllerAsync(new InMemoryJobStorageProvider(), SampleRuns());

        var onlyB = Insights.Json(await Stats(c, job: ["b"]));
        var both = Insights.Json(await Stats(c, job: ["a,b"]));
        var group = Insights.Json(await Stats(c, group: "RATES"));
        var none = Insights.Json(await Stats(c, group: "nope"));

        Assert.Equal(1, onlyB.GetProperty("totals").GetProperty("total").GetInt32());
        Assert.Equal(6, both.GetProperty("totals").GetProperty("total").GetInt32());
        Assert.Equal(6, group.GetProperty("totals").GetProperty("total").GetInt32());
        var empty = none.GetProperty("totals");
        Assert.Equal(0, empty.GetProperty("total").GetInt32());
        Assert.Empty(none.GetProperty("jobs").EnumerateArray());
        Assert.Equal(24, none.GetProperty("buckets").GetArrayLength());
    }

    [Fact]
    public async Task Stats_EmptyRange_HasNullRatesAndDurations()
    {
        var json = Insights.Json(await Stats(await ControllerAsync(new InMemoryJobStorageProvider())));

        var totals = json.GetProperty("totals");
        Assert.Equal(0, totals.GetProperty("total").GetInt32());
        foreach (var name in new[] { "successRate", "avgDuration", "p95Duration", "maxDuration", "lastFailureAt" })
            Assert.Equal(JsonValueKind.Null, totals.GetProperty(name).ValueKind);
    }

    [Fact]
    public async Task Stats_DefaultsToTheLast24Hours_AndClampsTheColumns()
    {
        var c = await ControllerAsync(new InMemoryJobStorageProvider(), Run("a", JobExecutionStatus.Completed, DateTime.UtcNow.AddHours(-1), 5));

        var json = Insights.Json(await Stats(c, since: null, until: null));
        var span = DateTime.Parse(json.GetProperty("until").GetString()!).ToUniversalTime()
            - DateTime.Parse(json.GetProperty("since").GetString()!).ToUniversalTime();

        Assert.Equal(TimeSpan.FromHours(24), span);
        Assert.Equal(24, json.GetProperty("buckets").GetArrayLength());
        Assert.Equal(1, json.GetProperty("totals").GetProperty("total").GetInt32());
        Assert.Single(Insights.Json(await Stats(c, buckets: "0")).GetProperty("buckets").EnumerateArray());
        Assert.Equal(200, Insights.Json(await Stats(c, buckets: "1000")).GetProperty("buckets").GetArrayLength());
        Assert.Equal(86_400_000 / 7.0, Insights.Json(await Stats(c, buckets: "7")).GetProperty("bucketMs").GetDouble(), 6);
    }

    [Fact]
    public async Task Stats_BadParameters_Answer400()
    {
        var c = await ControllerAsync(new InMemoryJobStorageProvider());

        Assert.Contains("since", Insights.Error(await Stats(c, since: "yesterday")));
        Assert.Contains("until", Insights.Error(await Stats(c, until: "2026-13-45")));
        Assert.Contains("after", Insights.Error(await Stats(c, since: Insights.UntilText, until: Insights.SinceText)));
        Assert.Contains("buckets", Insights.Error(await Stats(c, buckets: "many")));
    }

    [Fact]
    public void TimeBuckets_PutsTimesInTheirColumns_AndReadsTimesWithoutAnOffsetAsUtc()
    {
        var range = new TimeBuckets(Insights.Since, Insights.Until, 24);

        Assert.Equal(0, range.IndexOf(Insights.Since));
        Assert.Equal(0, range.IndexOf(Insights.Since.AddMinutes(59.99)));
        Assert.Equal(1, range.IndexOf(Insights.Since.AddHours(1)));
        Assert.Equal(23, range.IndexOf(Insights.Until.AddTicks(-1)));
        Assert.Equal(-1, range.IndexOf(Insights.Until));
        Assert.Equal(-1, range.IndexOf(Insights.Since.AddTicks(-1)));

        Assert.True(TimeBuckets.TryParseTime("2026-11-04T10:00:00", out var noOffset));
        Assert.Equal(new DateTime(2026, 11, 4, 10, 0, 0, DateTimeKind.Utc), noOffset);
        Assert.True(TimeBuckets.TryParseTime("2026-11-04T12:00:00+02:00", out var withOffset));
        Assert.Equal(new DateTime(2026, 11, 4, 10, 0, 0, DateTimeKind.Utc), withOffset);
    }
}

public class LogHistogramTests
{
    private static LogRingBuffer Buffer()
    {
        var buffer = new LogRingBuffer();
        void Add(int minutes, string level, string message, string source = "App") =>
            buffer.Add(new LogEntry { Timestamp = Insights.Since.AddMinutes(minutes), Level = level, Message = message, Source = source });

        Add(5, "INFO", "started");
        Add(10, "INFO", "fetched rates");
        Add(70, "WARN", "slow fetch");
        Add(80, "ERROR", "fetch failed", "RateBell.Fetch");
        Add(130, "DEBUG", "tick");
        Add(-5, "ERROR", "before the range");
        Add(24 * 60, "ERROR", "at the end (excluded)");
        return buffer;
    }

    private static Task<IActionResult> Histogram(ILogStore store, string? buckets = null, string? level = null, string? search = null,
        string? service = null, string? from = Insights.SinceText, string? to = Insights.UntilText) =>
        new LogsController(store).GetHistogram(from, to, buckets, level, search, service, CancellationToken.None);

    [Fact]
    public async Task MemoryHistogram_CountsLevelsPerColumn()
    {
        var json = Insights.Json(await Histogram(Buffer()));

        Assert.Equal("2026-11-04T00:00:00.000Z", json.GetProperty("from").GetString());
        Assert.Equal("2026-11-05T00:00:00.000Z", json.GetProperty("to").GetString());
        Assert.Equal(3_600_000, json.GetProperty("bucketMs").GetDouble());
        Assert.Equal("memory", json.GetProperty("store").GetString());
        var buckets = json.GetProperty("buckets").EnumerateArray().ToList();
        Assert.Equal(24, buckets.Count);
        Assert.Equal("2026-11-04T00:00:00.000Z", buckets[0].GetProperty("start").GetString());
        Assert.Equal(2, buckets[0].GetProperty("INFO").GetInt64());
        Assert.Equal(0, buckets[0].GetProperty("DEBUG").GetInt64());
        Assert.Equal(1, buckets[1].GetProperty("WARN").GetInt64());
        Assert.Equal(1, buckets[1].GetProperty("ERROR").GetInt64());
        Assert.Equal(1, buckets[2].GetProperty("DEBUG").GetInt64());
        var totals = json.GetProperty("totals");
        Assert.Equal(1, totals.GetProperty("DEBUG").GetInt64());
        Assert.Equal(2, totals.GetProperty("INFO").GetInt64());
        Assert.Equal(1, totals.GetProperty("WARN").GetInt64());
        Assert.Equal(1, totals.GetProperty("ERROR").GetInt64());
    }

    [Fact]
    public async Task MemoryHistogram_AppliesTheLogFilters()
    {
        var store = Buffer();

        var levels = Insights.Json(await Histogram(store, level: "warn,error")).GetProperty("totals");
        var search = Insights.Json(await Histogram(store, search: "RATEBELL.FETCH")).GetProperty("totals");
        var service = Insights.Json(await Histogram(store, service: "api")).GetProperty("totals");

        Assert.Equal((0, 0, 1, 1), (levels.GetProperty("DEBUG").GetInt64(), levels.GetProperty("INFO").GetInt64(),
            levels.GetProperty("WARN").GetInt64(), levels.GetProperty("ERROR").GetInt64()));
        Assert.Equal(1, search.GetProperty("ERROR").GetInt64());
        Assert.Equal(0, search.GetProperty("INFO").GetInt64());
        Assert.Equal(0, service.EnumerateObject().Sum(p => p.Value.GetInt64()));
    }

    [Fact]
    public async Task Histogram_BadParameters_Answer400()
    {
        var store = Buffer();

        Assert.Contains("Unknown level", Insights.Error(await Histogram(store, level: "loud")));
        Assert.Contains("from", Insights.Error(await Histogram(store, from: "nope")));
        Assert.Contains("to", Insights.Error(await Histogram(store, to: "nope")));
        Assert.Contains("after", Insights.Error(await Histogram(store, from: Insights.UntilText, to: Insights.SinceText)));
        Assert.Contains("buckets", Insights.Error(await Histogram(store, buckets: "x")));
        Assert.Equal(200, Insights.Json(await Histogram(store, buckets: "500")).GetProperty("buckets").GetArrayLength());
    }

    /// <summary>A store with only the required members: the histogram pages through QueryAsync.</summary>
    private sealed class PagingStore(List<LogRecord> records) : ILogStore
    {
        public List<LogQueryOptions> Queries { get; } = [];
        public string Name => "paging";
        public bool Persistent => true;

        public Task<LogPage> QueryAsync(LogQueryOptions options, CancellationToken cancellationToken = default)
        {
            Queries.Add(options);
            var matching = records.Where(r => (options.From == null || r.Timestamp >= options.From) && (options.To == null || r.Timestamp < options.To))
                .OrderByDescending(r => r.Timestamp).ToList();
            var page = matching.Skip(options.Offset).Take(options.Limit).ToList();
            return Task.FromResult(new LogPage { Entries = page, HasMore = options.Offset + page.Count < matching.Count });
        }

        public Task<IReadOnlyList<string>> GetServicesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    [Fact]
    public async Task DefaultHistogram_PagesThroughTheStore()
    {
        var records = Enumerable.Range(0, 1200).Select(i => new LogRecord
        {
            Timestamp = new DateTimeOffset(Insights.Since.AddMinutes(i)),
            Level = i % 2 == 0 ? LogLevels.Info : LogLevels.Error,
        }).ToList();
        var store = new PagingStore(records);

        var json = Insights.Json(await Histogram(store));

        Assert.Equal(600, json.GetProperty("totals").GetProperty("INFO").GetInt64());
        Assert.Equal(600, json.GetProperty("totals").GetProperty("ERROR").GetInt64());
        Assert.Equal(60, json.GetProperty("buckets")[0].GetProperty("INFO").GetInt64() + json.GetProperty("buckets")[0].GetProperty("ERROR").GetInt64());
        Assert.Equal(3, store.Queries.Count);
        Assert.All(store.Queries, q => Assert.Equal(new DateTimeOffset(Insights.Since), q.From));
        Assert.Equal("paging", json.GetProperty("store").GetString());
    }

    private sealed class FakeSeq(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(answer(request));
        }

        public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private static SeqLogsOptions SeqOptions() => new() { ServerUrl = "http://seq.test:5341", ApiKey = "key" };

    [Fact]
    public async Task SeqHistogram_AsksSeqToCount_AndMapsTheRows()
    {
        var seq = new FakeSeq(_ => FakeSeq.Json("""
            {
              "Columns": ["@Level", "(@Timestamp - (DateTime('2026-11-04T00:00:00.0000000Z')) - (@...", "count"],
              "Rows": [
                ["Information", 0, 120], ["info", 1e-9, 5], ["Warning", 1, 3], ["Error", 1, 1], ["Fatal", 1, 2],
                ["Debug", 23, 4], ["Error", 24, 9], ["Error", -1, 9]
              ]
            }
            """));
        using var store = new SeqLogStore(SeqOptions(), "api", seq);

        var json = Insights.Json(await Histogram(store, level: "INFO,WARN,ERROR,DEBUG", search: "it's", service: "ratebell-api"));

        var request = seq.Requests.Single();
        var uri = Uri.UnescapeDataString(request.RequestUri!.ToString());
        Assert.StartsWith("http://seq.test:5341/api/data?q=select count(*) as count from stream where ", uri);
        Assert.Contains("Contains(@Message, 'it''s') ci", uri);
        Assert.Contains("Service = 'ratebell-api'", uri);
        Assert.DoesNotContain("@Level in", uri); // every level: no level clause
        const string elapsed = "(@Timestamp - DateTime('2026-11-04T00:00:00.0000000Z'))";
        Assert.Contains($"group by @Level, ({elapsed} - {elapsed} % 36000000000) / 36000000000 limit 10000", uri);
        Assert.Contains("rangeStartUtc=2026-11-04T00:00:00.0000000Z", uri);
        Assert.Contains("rangeEndUtc=2026-11-05T00:00:00.0000000Z", uri);
        Assert.Equal("key", request.Headers.GetValues("X-Seq-ApiKey").Single());

        Assert.Equal("seq", json.GetProperty("store").GetString());
        var buckets = json.GetProperty("buckets");
        Assert.Equal(125, buckets[0].GetProperty("INFO").GetInt64());
        Assert.Equal(3, buckets[1].GetProperty("WARN").GetInt64());
        Assert.Equal(3, buckets[1].GetProperty("ERROR").GetInt64());
        Assert.Equal(4, buckets[23].GetProperty("DEBUG").GetInt64());
        Assert.Equal(3, json.GetProperty("totals").GetProperty("ERROR").GetInt64()); // columns -1 and 24 are outside
    }

    /// <summary>Against a real Seq when <c>QCLI_TEST_SEQ_URL</c> is set (see SeqLogsPluginTests); otherwise it does nothing.</summary>
    [Fact]
    public async Task RealSeq_CountsPerLevelAndColumn()
    {
        var url = Environment.GetEnvironmentVariable("QCLI_TEST_SEQ_URL");
        if (string.IsNullOrEmpty(url)) return;

        var service = "qcli-histogram-" + Guid.NewGuid().ToString("N")[..8];
        var since = DateTime.UtcNow.AddHours(-3);
        string Event(int minutes, string? level, string message)
        {
            var e = new Dictionary<string, object?> { ["@t"] = since.AddMinutes(minutes).ToString("O"), ["@m"] = message, ["Service"] = service };
            if (level != null) e["@l"] = level; // no level: Information
            return JsonSerializer.Serialize(e);
        }
        var clef = string.Join('\n', Event(10, "Warning", "slow"), Event(70, "Error", "it's broken"), Event(75, "Fatal", "down"),
            Event(80, null, "fine"), Event(179, "Debug", "tick"));
        using (var http = new HttpClient { BaseAddress = new Uri(url.TrimEnd('/') + "/") })
        {
            var ingest = await http.PostAsync("ingest/clef", new StringContent(clef, Encoding.UTF8, "application/vnd.serilog.clef"));
            Assert.True(ingest.IsSuccessStatusCode);
        }

        using var store = new SeqLogStore(new SeqLogsOptions { ServerUrl = url }, service);
        var options = new LogHistogramOptions
        {
            Range = new TimeBuckets(since, since.AddHours(3), 3),
            Filter = new LogQueryOptions { Service = service },
        };
        LogHistogram histogram = new(options.Range);
        for (var i = 0; i < 50 && histogram.Totals[LogLevels.Debug] == 0; i++)
        {
            await Task.Delay(100);
            histogram = await store.HistogramAsync(options);
        }

        Assert.Equal(1, histogram.Buckets[0][LogLevels.Warn]);
        Assert.Equal(2, histogram.Buckets[1][LogLevels.Error]);
        Assert.Equal(1, histogram.Buckets[1][LogLevels.Info]);
        Assert.Equal(1, histogram.Buckets[2][LogLevels.Debug]);
        var errors = await store.HistogramAsync(new LogHistogramOptions
        {
            Range = options.Range,
            Filter = new LogQueryOptions { Service = service, Levels = [LogLevels.Error], Search = "IT'S" },
        });
        Assert.Equal(1, errors.Totals[LogLevels.Error]);
        Assert.Equal(0, errors.Totals[LogLevels.Info]);
    }

    [Fact]
    public void SeqHistogramQuery_FiltersLevels_AndUsesFractionalWidths()
    {
        var query = SeqLogStore.HistogramQuery(new LogHistogramOptions
        {
            Range = new TimeBuckets(Insights.Since, Insights.Until, 7),
            Filter = new LogQueryOptions { Levels = [LogLevels.Error] },
        });

        Assert.Contains("where @Level in ['Error', 'Fatal'", query);
        Assert.Contains("% 123428571428.571) / 123428571428.571 limit", query);
    }

    [Fact]
    public async Task SeqHistogram_SeqDownOrFailing_Answers502()
    {
        using var down = new SeqLogStore(SeqOptions(), "api", new FakeSeq(_ => throw new HttpRequestException("refused")));
        using var failing = new SeqLogStore(SeqOptions(), "api", new FakeSeq(_ => FakeSeq.Json("""{ "Error": "Syntax error" }""", HttpStatusCode.BadRequest)));
        using var queryError = new SeqLogStore(SeqOptions(), "api", new FakeSeq(_ => FakeSeq.Json("""{ "Error": "The query is invalid" }""")));

        Assert.Contains("refused", Insights.Error(await Histogram(down), 502));
        Assert.Contains("Syntax error", Insights.Error(await Histogram(failing), 502));
        Assert.Contains("The query is invalid", Insights.Error(await Histogram(queryError), 502));
    }
}

public class StatusHistoryTests
{
    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (StatusHistory History, Clock Clock, Func<TimeSpan?> Cpu) Create(int capacity, Func<TimeSpan?> cpu)
    {
        var clock = new Clock(new DateTimeOffset(Insights.Since));
        var services = new ServiceCollection().BuildServiceProvider();
        return (new StatusHistory(services, capacity, clock, cpu, () => 254.0), clock, cpu);
    }

    [Fact]
    public void Samples_AreARingBuffer_OldestFirst()
    {
        var (history, clock, _) = Create(3, () => TimeSpan.Zero);

        for (var i = 0; i < 5; i++)
        {
            history.TakeSample();
            clock.Now += StatusHistory.Interval;
        }

        var samples = history.GetSamples(60);
        Assert.Equal(3, samples.Count);
        Assert.Equal(Insights.Since.AddSeconds(30), samples[0].Timestamp);
        Assert.Equal(Insights.Since.AddSeconds(60), samples[2].Timestamp);
        Assert.Equal(254.0, samples[0].MemoryMb);
        Assert.Equal(5760, StatusHistory.DefaultCapacity);
    }

    [Fact]
    public void CpuPercent_IsTheCpuTimeOverTheWallTime_NullFirst()
    {
        var cpu = TimeSpan.Zero;
        var (history, clock, _) = Create(10, () => cpu);

        var first = history.TakeSample();
        clock.Now += TimeSpan.FromSeconds(15);
        cpu = TimeSpan.FromSeconds(1.5);
        var second = history.TakeSample();
        clock.Now += TimeSpan.FromSeconds(15);
        cpu = TimeSpan.FromSeconds(31.5); // two cores busy
        var third = history.TakeSample();

        Assert.Null(first.CpuPercent);
        Assert.Equal(10.0, second.CpuPercent);
        Assert.Equal(200.0, third.CpuPercent);
    }

    [Fact]
    public void CpuPercent_IsNullWhenThePlatformCannotTell()
    {
        var (history, clock, _) = Create(10, () => null);

        history.TakeSample();
        clock.Now += TimeSpan.FromSeconds(15);

        Assert.Null(history.TakeSample().CpuPercent);
    }

    [Fact]
    public void GetSamples_KeepsTheLastMinutes()
    {
        var (history, clock, _) = Create(100, () => TimeSpan.Zero);
        for (var i = 0; i < 20; i++)
        {
            history.TakeSample();
            clock.Now += TimeSpan.FromMinutes(1);
        }

        // now = since + 20 min; the last 5 minutes hold the samples of minutes 15..19
        Assert.Equal(5, history.GetSamples(5).Count);
        Assert.Equal(20, history.GetSamples(60).Count);
    }

    private static StatusController Controller(IServiceProvider services) => new(
        new CliEventSocketManager(NullLogger<CliEventSocketManager>.Instance),
        new CliCommandRegistry(NullLogger<CliCommandRegistry>.Instance),
        services);

    [Fact]
    public void HistoryEndpoint_ClampsMinutes_AndRefusesNonNumbers()
    {
        var clock = new Clock(DateTimeOffset.UtcNow);
        var history = new StatusHistory(new ServiceCollection().BuildServiceProvider(), 10_000, clock, () => TimeSpan.Zero, () => 1);
        for (var i = 2999; i >= 0; i--)
        {
            history.Add(new StatusSample(clock.Now.UtcDateTime.AddSeconds(-30 * i), 1, null, 0, 0));
        }
        var services = new ServiceCollection().AddSingleton(history).BuildServiceProvider();
        var controller = Controller(services);

        var json = Insights.Json(controller.GetHistory());
        Assert.Equal(15, json.GetProperty("intervalSeconds").GetInt32());
        Assert.Equal(121, json.GetProperty("samples").GetArrayLength()); // 60 minutes of 30-second samples, both ends
        Assert.Equal(3, Insights.Json(controller.GetHistory("0")).GetProperty("samples").GetArrayLength()); // 1 minute
        Assert.Equal(2881, Insights.Json(controller.GetHistory("99999")).GetProperty("samples").GetArrayLength()); // 1440 minutes
        Assert.Contains("minutes", Insights.Error(controller.GetHistory("an hour")));

        var samples = json.GetProperty("samples").EnumerateArray().ToList();
        Assert.True(string.CompareOrdinal(samples[0].GetProperty("timestamp").GetString(), samples[^1].GetProperty("timestamp").GetString()) < 0);
        var sample = samples[0];
        foreach (var name in new[] { "timestamp", "memoryMb", "cpuPercent", "activeWsConnections", "activeShellSessions" })
            Assert.True(sample.TryGetProperty(name, out _), name);
    }

    [Fact]
    public void HistoryEndpoint_WithoutTheSampler_IsEmpty()
    {
        var json = Insights.Json(Controller(new ServiceCollection().BuildServiceProvider()).GetHistory());

        Assert.Equal(0, json.GetProperty("samples").GetArrayLength());
    }
}

public class CommandUsageTests
{
    private static (CliCommandUsage Usage, CliCommandExecutorService Executor, CliController Controller) Create()
    {
        var registry = new CliCommandRegistry(NullLogger<CliCommandRegistry>.Instance);
        registry.Register(new TestProcessor("echo", "Echo"));
        registry.Register(new TestProcessor("boom", "Throws", (_, _) => throw new InvalidOperationException("boom")));
        registry.Register(new TestStreamProcessor());
        var usage = new CliCommandUsage();
        var executor = new CliCommandExecutorService(registry, NullLogger<CliCommandExecutorService>.Instance, [], null, usage);
        var controller = new CliController(registry, executor, new CliServerInfoService(), NullLogger<CliController>.Instance, null, usage)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { Response = { Body = new MemoryStream() } } },
        };
        return (usage, executor, controller);
    }

    [Fact]
    public async Task Executions_AreCountedPerRootCommand_WithFailuresAndUnknownCommands()
    {
        var (_, executor, controller) = Create();

        await executor.ExecuteAsync(new CliProcessCommand { Command = "echo" });
        await executor.ExecuteAsync(new CliProcessCommand { Command = "ECHO" });
        await executor.ExecuteAsync(new CliProcessCommand { Command = "boom" });
        await executor.ExecuteAsync(new CliProcessCommand { Command = "nope" });

        var json = Insights.Json(controller.GetCommandStats());
        Assert.NotNull(json.GetProperty("since").GetString());
        var commands = json.GetProperty("commands").EnumerateArray().ToList();
        Assert.Equal(["echo", "boom", "nope"], commands.Select(c => c.GetProperty("command").GetString()));

        var echo = commands[0];
        Assert.True(echo.GetProperty("known").GetBoolean());
        Assert.Equal(2, echo.GetProperty("executions").GetInt64());
        Assert.Equal(0, echo.GetProperty("failures").GetInt64());
        Assert.True(echo.GetProperty("avgDuration").GetDouble() >= 0);
        Assert.EndsWith("Z", echo.GetProperty("lastExecutedAt").GetString());
        Assert.Equal(JsonValueKind.Null, echo.GetProperty("lastFailedAt").ValueKind);

        var boom = commands[1];
        Assert.True(boom.GetProperty("known").GetBoolean());
        Assert.Equal(1, boom.GetProperty("failures").GetInt64());
        Assert.NotNull(boom.GetProperty("lastFailedAt").GetString());

        var nope = commands[2];
        Assert.False(nope.GetProperty("known").GetBoolean());
        Assert.Equal(1, nope.GetProperty("failures").GetInt64());
    }

    [Fact]
    public async Task StreamedExecutions_AreCountedOnce()
    {
        var (usage, _, controller) = Create();

        await controller.ExecuteStream(new CliProcessCommand { Command = "stream-test" }, CancellationToken.None);
        await controller.ExecuteStream(new CliProcessCommand { Command = "echo" }, CancellationToken.None); // through the executor
        await controller.ExecuteStream(new CliProcessCommand { Command = "missing" }, CancellationToken.None);

        var entries = usage.GetEntries().ToDictionary(e => e.Command);
        Assert.Equal(1, entries["stream-test"].Executions);
        Assert.Equal(0, entries["stream-test"].Failures);
        Assert.Equal(1, entries["echo"].Executions);
        Assert.False(entries["missing"].Known);
        Assert.Equal(1, entries["missing"].Failures);
    }

    [Fact]
    public void Record_IsThreadSafe_AndSortsByExecutionsThenName()
    {
        var usage = new CliCommandUsage();

        Parallel.For(0, 1000, i => usage.Record(i % 4 == 0 ? "b cmd" : "a", true, TimeSpan.FromMilliseconds(2), i % 10 == 0));
        usage.Record("c", false, TimeSpan.Zero, false);
        usage.Record("d", false, TimeSpan.Zero, false);
        usage.Record("  ", false, TimeSpan.Zero, false);

        var entries = usage.GetEntries();
        Assert.Equal(["a", "b", "c", "d"], entries.Select(e => e.Command));
        Assert.Equal(750, entries[0].Executions);
        Assert.Equal(250, entries[1].Executions);
        Assert.Equal(100, entries.Sum(e => e.Failures));
        Assert.Equal(2, entries[0].AvgDuration);
    }
}

public class AuditTimeFilterTests
{
    private static AuditEntry Entry(string @event, long timestamp) => new() { Timestamp = timestamp, Event = @event, Username = "bob" };

    private static async Task Seed(IAuditStore store)
    {
        await store.LogAsync(Entry("login", 1000));
        await store.LogAsync(Entry("login", 2000));
        await store.LogAsync(Entry("logout", 2500));
        await store.LogAsync(Entry("login", 3000));
    }

    private static async Task AssertFilters(IAuditStore store)
    {
        await Seed(store);

        var range = await store.GetEntriesAsync(new AuditQueryOptions
        {
            From = DateTimeOffset.FromUnixTimeMilliseconds(2000),
            To = DateTimeOffset.FromUnixTimeMilliseconds(3000),
        });
        var combined = await store.GetEntriesAsync(new AuditQueryOptions { Event = "login", From = DateTimeOffset.FromUnixTimeMilliseconds(1500) });
        var open = await store.GetEntriesAsync(new AuditQueryOptions { To = DateTimeOffset.FromUnixTimeMilliseconds(1000) });

        Assert.Equal([2500L, 2000L], range.Select(e => e.Timestamp));
        Assert.Equal([3000L, 2000L], combined.Select(e => e.Timestamp));
        Assert.Empty(open);
    }

    [Fact]
    public Task InMemoryStore_FiltersByTime() => AssertFilters(new InMemoryAuditStore());

    [Fact]
    public async Task EfCoreStore_FiltersByTime()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<AuditStorageDbContext>().UseSqlite(connection).Options;
        await AssertFilters(new EfCoreAuditStore(new SqliteContextFactory<AuditStorageDbContext>(options, o => new AuditStorageDbContext(o))));
    }

    [Fact]
    public async Task Controller_AcceptsIsoAndEpochMilliseconds_AndRefusesOthers()
    {
        var store = new InMemoryAuditStore();
        var day = new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        await store.LogAsync(Entry("a", day - 1));
        await store.LogAsync(Entry("b", day));
        await store.LogAsync(Entry("c", day + 3_600_000));
        var controller = new CliAuditController(store);

        async Task<string[]> Events(string? from, string? to)
        {
            var ok = Assert.IsType<OkObjectResult>(await controller.GetEntries(null, null, null, null, null, from, to));
            return ((List<AuditEntry>)ok.Value!).Select(e => e.Event).ToArray();
        }

        Assert.Equal(["c", "b"], await Events("2026-11-05T00:00:00Z", null));
        Assert.Equal(["c", "b"], await Events("2026-11-05T00:00:00", null)); // no offset: UTC
        Assert.Equal(["b"], await Events(day.ToString(), (day + 3_600_000).ToString()));
        Assert.Equal(["a"], await Events(null, "2026-11-05T02:00:00+02:00"));
        Assert.Contains("from", Insights.Error(await controller.GetEntries(null, null, null, null, null, "soon", null)));
        Assert.Contains("to", Insights.Error(await controller.GetEntries(null, null, null, null, null, null, "later")));
    }
}

/// <summary>The new routes in a real host: literal routes win over <c>{id}</c>, and the sampler runs with the host.</summary>
public class InsightsRoutingTests : IAsyncLifetime
{
    private IHost _host = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services => services.AddControllers().AddCli(cli =>
                {
                    cli.AddProcessor(new TestProcessor("stats", "A command named stats"));
                    cli.AddProcessor(new TestProcessor("echo", "Echo"));
                    cli.AddJob(new DummyJob(), o => { o.Name = "quick"; o.Interval = TimeSpan.FromHours(1); });
                    cli.AddAdmin();
                }));
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                });
            })
            .StartAsync();
        _client = _host.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    private async Task<JsonElement> GetJsonAsync(string path)
    {
        var response = await _client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task JobStats_IsNotTakenForAJobId()
    {
        var json = await GetJsonAsync("/api/v1/qcli/jobs/stats?buckets=12");

        Assert.Equal(12, json.GetProperty("buckets").GetArrayLength());
        Assert.True(json.TryGetProperty("totals", out _));
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/api/v1/qcli/jobs/stats?since=nope")).StatusCode);
    }

    [Fact]
    public async Task CommandStats_IsNotTakenForACommandNamedStats()
    {
        (await _client.PostAsJsonAsync("/api/v1/qcli/execute", new { command = "echo" })).EnsureSuccessStatusCode();
        (await _client.PostAsJsonAsync("/api/v1/qcli/execute", new { command = "stats" })).EnsureSuccessStatusCode();

        var json = await GetJsonAsync("/api/v1/qcli/commands/stats");

        var commands = json.GetProperty("commands").EnumerateArray().Select(c => c.GetProperty("command").GetString()).ToList();
        Assert.Contains("echo", commands);
        Assert.Contains("stats", commands);
        Assert.Equal(JsonValueKind.Array, (await GetJsonAsync("/api/v1/qcli/commands")).ValueKind);
    }

    [Fact]
    public async Task StatusHistory_HasTheSampleTakenAtStart()
    {
        var json = await GetJsonAsync("/api/v1/qcli/status/history");

        Assert.Equal(15, json.GetProperty("intervalSeconds").GetInt32());
        var sample = Assert.Single(json.GetProperty("samples").EnumerateArray());
        Assert.True(sample.GetProperty("memoryMb").GetDouble() > 0);
        Assert.Equal(JsonValueKind.Null, sample.GetProperty("cpuPercent").ValueKind);
    }

    [Fact]
    public async Task LogHistogram_IsServed()
    {
        var json = await GetJsonAsync("/api/v1/qcli/logs/histogram?buckets=4");

        Assert.Equal(4, json.GetProperty("buckets").GetArrayLength());
        Assert.Equal("memory", json.GetProperty("store").GetString());
        Assert.True(json.GetProperty("buckets")[0].TryGetProperty("INFO", out _));
    }
}
