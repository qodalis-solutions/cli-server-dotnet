using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Qodalis.Cli.Abstractions.Logs;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Models;
using Qodalis.Cli.Plugin.Admin.Extensions;
using Qodalis.Cli.Plugin.Admin.Services;
using Qodalis.Cli.Plugin.Logs.Seq;
using Qodalis.Cli.Plugin.Logs.Seq.Extensions;

namespace Qodalis.Cli.Tests;

public class SeqLogsPluginTests
{
    private const string Url = "http://seq.test:5341";

    /// <summary>Answers requests from a function and records them.</summary>
    private sealed class FakeSeq(Func<HttpRequestMessage, string, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Requests) Requests.Add((request, body));
            return answer(request, body);
        }

        public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private static SeqLogsOptions Options(Action<SeqLogsOptions>? configure = null)
    {
        var options = new SeqLogsOptions { ServerUrl = Url, ApiKey = "key", FlushInterval = TimeSpan.Zero };
        configure?.Invoke(options);
        return options;
    }

    private static ServiceProvider Build(Action<CliBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers().AddCli(configure);
        return services.BuildServiceProvider();
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    #region Registration

    [Fact]
    public void AddSeqLogs_MakesSeqTheHistory_BeforeOrAfterTheAdminPlugin_AndIsListed()
    {
        using var before = Build(cli => cli.AddSeqLogs(o => o.ServerUrl = Url).AddAdmin());
        using var after = Build(cli => cli.AddAdmin().AddSeqLogs(o => o.ServerUrl = Url));
        using var memory = Build(cli => cli.AddAdmin());

        Assert.Equal("seq", before.GetRequiredService<ILogStore>().Name);
        Assert.Equal("seq", after.GetRequiredService<ILogStore>().Name);
        Assert.IsType<LogRingBuffer>(memory.GetRequiredService<ILogStore>());
        Assert.Contains(before.GetRequiredService<CliInstalledPlugins>().All, p => p.Id == "Qodalis.Cli.Plugin.Logs.Seq");
    }

    [Fact]
    public void AddSeqLogs_WithoutReadHistory_OnlyWrites()
    {
        using var sp = Build(cli => cli.AddAdmin().AddSeqLogs(o => { o.ServerUrl = Url; o.ReadHistory = false; }));

        Assert.IsType<LogRingBuffer>(sp.GetRequiredService<ILogStore>());
        Assert.Contains(sp.GetServices<ILoggerProvider>(), p => p is SeqLoggerProvider);
    }

    [Theory]
    [InlineData("")]
    [InlineData("seq:5341")]
    [InlineData("ftp://seq")]
    public void AddSeqLogs_RefusesABadServerUrl(string url)
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentException>(() => services.AddControllers().AddCli(cli => cli.AddSeqLogs(o => o.ServerUrl = url)));
    }

    #endregion

    #region Writing

    [Fact]
    public void ToClef_HasTheMessage_Template_Level_Values_Source_AndService()
    {
        using var shipper = new SeqLogShipper(Options(), new FakeSeq((_, _) => new HttpResponseMessage(HttpStatusCode.Created)));
        var provider = new SeqLoggerProvider(shipper, Options(), "ratebell-jobs");
        var state = new List<KeyValuePair<string, object?>>
        {
            new("Bank", "ing"), new("Count", 5), new("@odd", true), new("{OriginalFormat}", "Fetched {Bank}: {Count} rates"),
        };

        var clef = provider.ToClef(
            new DateTimeOffset(2026, 11, 5, 20, 0, 0, TimeSpan.Zero), "RateBell.Fetch", LogLevel.Critical, new EventId(7, "Fetched"),
            state, new InvalidOperationException("boom"), (_, _) => "Fetched ing: 5 rates");

        using var json = JsonDocument.Parse(clef);
        var e = json.RootElement;
        Assert.Equal("2026-11-05T20:00:00.0000000Z", e.GetProperty("@t").GetString());
        Assert.Equal("Fetched ing: 5 rates", e.GetProperty("@m").GetString());
        Assert.Equal("Fetched {Bank}: {Count} rates", e.GetProperty("@mt").GetString());
        Assert.Equal("Fatal", e.GetProperty("@l").GetString());
        Assert.Contains("boom", e.GetProperty("@x").GetString());
        Assert.Equal("ing", e.GetProperty("Bank").GetString());
        Assert.Equal(5, e.GetProperty("Count").GetInt32());
        Assert.True(e.GetProperty("@@odd").GetBoolean());
        Assert.Equal(7, e.GetProperty("EventId").GetProperty("Id").GetInt32());
        Assert.Equal("RateBell.Fetch", e.GetProperty("SourceContext").GetString());
        Assert.Equal("ratebell-jobs", e.GetProperty("Service").GetString());
        Assert.DoesNotContain("\n", clef);
    }

    [Fact]
    public async Task Logging_SendsBatchesToIngest_WithTheKey_AndSkipsLevelsBelowTheMinimum()
    {
        var seq = new FakeSeq((_, _) => new HttpResponseMessage(HttpStatusCode.Created));
        var options = Options(o => o.MinimumLevel = LogLevel.Warning);
        using var shipper = new SeqLogShipper(options, seq);
        using var factory = LoggerFactory.Create(l => l.SetMinimumLevel(LogLevel.Trace).AddProvider(new SeqLoggerProvider(shipper, options, "api")));
        var logger = factory.CreateLogger("Test");

        logger.LogInformation("skipped");
        logger.LogWarning("first {N}", 1);
        logger.LogError("second");
        await WaitForAsync(() => seq.Requests.Sum(r => r.Body.Split('\n').Length) >= 2);

        var (request, body) = seq.Requests[0];
        Assert.Equal($"{Url}/ingest/clef", request.RequestUri!.ToString());
        Assert.Equal("key", request.Headers.GetValues("X-Seq-ApiKey").Single());
        Assert.Equal("application/vnd.serilog.clef", request.Content!.Headers.ContentType!.MediaType);
        var all = string.Join('\n', seq.Requests.Select(r => r.Body));
        Assert.Contains("first 1", all);
        Assert.Contains("second", all);
        Assert.DoesNotContain("skipped", all);
    }

    [Fact]
    public async Task WhileSeqIsDown_TheBatchIsRetried()
    {
        var calls = 0;
        var seq = new FakeSeq((_, _) => Interlocked.Increment(ref calls) == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : new HttpResponseMessage(HttpStatusCode.Created));
        var errors = new StringWriter();
        using var shipper = new SeqLogShipper(Options(), seq, errors);

        shipper.Enqueue("{\"@m\":\"kept\"}");
        await WaitForAsync(() => Volatile.Read(ref calls) >= 2);

        Assert.All(seq.Requests, r => Assert.Contains("kept", r.Body));
        Assert.Contains("503", errors.ToString());
    }

    [Fact]
    public async Task ARejectedBatch_IsDropped_SoLaterEventsStillGo()
    {
        var seq = new FakeSeq((_, body) => body.Contains("bad")
            ? new HttpResponseMessage(HttpStatusCode.BadRequest)
            : new HttpResponseMessage(HttpStatusCode.Created));
        using var shipper = new SeqLogShipper(Options(o => o.BatchSize = 1), seq, new StringWriter());

        shipper.Enqueue("{\"@m\":\"bad\"}");
        await WaitForAsync(() => seq.Requests.Count >= 1);
        shipper.Enqueue("{\"@m\":\"good\"}");
        await WaitForAsync(() => seq.Requests.Any(r => r.Body.Contains("good")));

        Assert.Single(seq.Requests, r => r.Body.Contains("bad"));
    }

    [Fact]
    public void Dispose_SendsWhatIsQueued()
    {
        var seq = new FakeSeq((_, _) => new HttpResponseMessage(HttpStatusCode.Created));
        var shipper = new SeqLogShipper(Options(o => o.FlushInterval = TimeSpan.FromMinutes(5)), seq);

        shipper.Enqueue("{\"@m\":\"last words\"}");
        shipper.Dispose();

        Assert.Contains(seq.Requests, r => r.Body.Contains("last words"));
    }

    #endregion

    #region Reading

    [Fact]
    public void Filter_CombinesLevels_Search_AndService_EscapingQuotes()
    {
        var filter = SeqLogStore.Filter(new LogQueryOptions
        {
            Levels = [LogLevels.Warn, LogLevels.Error],
            Search = "it's 100%",
            Service = "ratebell-api",
        });

        Assert.StartsWith("@Level in ['Warning', 'Warn', 'warning', 'warn', 'Error', 'Fatal',", filter);
        Assert.Contains("(Contains(@Message, 'it''s 100%') ci or Contains(SourceContext, 'it''s 100%') ci)", filter);
        Assert.EndsWith("Service = 'ratebell-api'", filter);
        Assert.Null(SeqLogStore.Filter(new LogQueryOptions { Levels = LogLevels.All }));
    }

    private const string TwoEvents = """
        [
          { "Id": "event-2", "Timestamp": "2026-11-05T20:07:13.0000000Z", "Level": "Error", "RenderedMessage": "It's broken",
            "Exception": "System.Exception: boom",
            "Properties": [ { "Name": "Service", "Value": "ratebell-api" }, { "Name": "SourceContext", "Value": "RateBell.Api" } ] },
          { "Id": "event-1", "Timestamp": "2026-11-05T20:07:12.0000000Z", "Level": "Information", "RenderedMessage": "Fetched ing: 5 rates",
            "Properties": [ { "Name": "Service", "Value": "ratebell-jobs" }, { "Name": "Count", "Value": 5 } ] }
        ]
        """;

    [Fact]
    public async Task QueryAsync_AsksSeqForOneMoreThanThePage_AndMapsTheEvents()
    {
        var seq = new FakeSeq((_, _) => FakeSeq.Json(TwoEvents));
        using var store = new SeqLogStore(Options(), "ratebell-api", seq);

        var page = await store.QueryAsync(new LogQueryOptions
        {
            Limit = 1,
            Service = "ratebell-api",
            From = new DateTimeOffset(2026, 11, 5, 19, 0, 0, TimeSpan.Zero),
            To = new DateTimeOffset(2026, 11, 5, 21, 0, 0, TimeSpan.FromHours(1)),
        });

        var uri = Uri.UnescapeDataString(seq.Requests.Single().Request.RequestUri!.ToString());
        Assert.StartsWith($"{Url}/api/events?count=2&render=true&filter=Service = 'ratebell-api'", uri);
        Assert.Contains("fromDateUtc=2026-11-05T19:00:00.0000000Z", uri);
        Assert.Contains("toDateUtc=2026-11-05T20:00:00.0000000Z", uri);
        Assert.Equal("key", seq.Requests.Single().Request.Headers.GetValues("X-Seq-ApiKey").Single());
        Assert.True(page.HasMore);
        Assert.Null(page.Total);
        var entry = Assert.Single(page.Entries);
        Assert.Equal(LogLevels.Error, entry.Level);
        Assert.Equal("It's broken", entry.Message);
        Assert.Equal("RateBell.Api", entry.Source);
        Assert.Equal("ratebell-api", entry.Service);
        Assert.Equal("System.Exception: boom", entry.Exception);
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 20, 7, 13, TimeSpan.Zero), entry.Timestamp);
    }

    [Fact]
    public async Task QueryAsync_SkipsTheOffset_AndStopsAtTheMaximumDepth()
    {
        var seq = new FakeSeq((_, _) => FakeSeq.Json(TwoEvents));
        using var store = new SeqLogStore(Options(), "api", seq);

        var second = await store.QueryAsync(new LogQueryOptions { Limit = 1, Offset = 1 });
        var tooDeep = await store.QueryAsync(new LogQueryOptions { Offset = SeqLogStore.MaxDepth });

        Assert.Equal("Fetched ing: 5 rates", Assert.Single(second.Entries).Message);
        Assert.False(second.HasMore);
        Assert.Empty(tooDeep.Entries);
        Assert.Single(seq.Requests);
    }

    [Fact]
    public async Task QueryAsync_SeqErrors_Throw()
    {
        var seq = new FakeSeq((_, _) => FakeSeq.Json("""{ "Error": "The filter is invalid." }""", HttpStatusCode.BadRequest));
        using var store = new SeqLogStore(Options(), "api", seq);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => store.QueryAsync(new LogQueryOptions()));

        Assert.Contains("The filter is invalid.", error.Message);
    }

    [Fact]
    public async Task GetServicesAsync_ListsTheServicesInSeq_WithThisOne_OrOnlyThisOneWhenSeqFails()
    {
        var seq = new FakeSeq((_, _) => FakeSeq.Json("""{ "Columns": ["service"], "Rows": [["ratebell-jobs"], ["ratebell-api"], [null]] }"""));
        using var store = new SeqLogStore(Options(), "admin", seq);
        using var down = new SeqLogStore(Options(), "admin", new FakeSeq((_, _) => throw new HttpRequestException("refused")));

        Assert.Equal(["admin", "ratebell-api", "ratebell-jobs"], await store.GetServicesAsync());
        Assert.Equal(["admin", "ratebell-api", "ratebell-jobs"], await store.GetServicesAsync());
        Assert.Single(seq.Requests);
        Assert.Equal(["admin"], await down.GetServicesAsync());
    }

    #endregion

    /// <summary>
    /// Against a real Seq when <c>QCLI_TEST_SEQ_URL</c> is set (e.g. <c>docker run -e ACCEPT_EULA=Y
    /// -e SEQ_FIRSTRUN_NOAUTHENTICATION=true -p 5341:80 datalust/seq</c>); otherwise it does nothing.
    /// </summary>
    [Fact]
    public async Task RealSeq_WritesAndReadsBack()
    {
        var url = Environment.GetEnvironmentVariable("QCLI_TEST_SEQ_URL");
        if (string.IsNullOrEmpty(url)) return;

        var service = "qcli-test-" + Guid.NewGuid().ToString("N")[..8];
        var options = new SeqLogsOptions { ServerUrl = url, FlushInterval = TimeSpan.Zero };
        using (var shipper = new SeqLogShipper(options))
        using (var factory = LoggerFactory.Create(l => l.AddProvider(new SeqLoggerProvider(shipper, options, service))))
        {
            factory.CreateLogger("Qodalis.Test").LogError(new InvalidOperationException("boom"), "It's {What} 100%", "broken");
        }

        using var store = new SeqLogStore(options, service);
        LogPage page = new();
        for (var i = 0; i < 50 && page.Entries.Count == 0; i++)
        {
            await Task.Delay(100);
            page = await store.QueryAsync(new LogQueryOptions { Service = service, Levels = [LogLevels.Error], Search = "IT'S BROKEN 100%" });
        }

        var entry = Assert.Single(page.Entries);
        Assert.Equal("It's broken 100%", entry.Message);
        Assert.Equal("Qodalis.Test", entry.Source);
        Assert.Contains("boom", entry.Exception);
        Assert.Contains(service, await store.GetServicesAsync());
        Assert.Empty((await store.QueryAsync(new LogQueryOptions { Service = service, Levels = [LogLevels.Info] })).Entries);
    }
}
