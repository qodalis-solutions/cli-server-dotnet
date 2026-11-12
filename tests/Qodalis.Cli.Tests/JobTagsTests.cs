using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Qodalis.Cli.Abstractions.Jobs;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Plugin.Jobs;

namespace Qodalis.Cli.Tests;

/// <summary>Job tags: normalization, the <c>?tag=</c> filter, updates and the MCP <c>jobs_list</c> filter.</summary>
public class JobTagsTests
{
    private const string Jobs = "/api/v1/qcli/jobs";

    private static Task<McpPlainHost> StartAsync() => McpPlainHost.StartAsync(cli =>
    {
        cli.AddJob(new McpNoopJob(), o => { o.Id = "ro-bnr"; o.Name = "bnr"; o.Interval = TimeSpan.FromHours(1); o.Tags = ["country:ro", "Official", " official "]; });
        cli.AddJob(new McpNoopJob(), o => { o.Id = "ro-bt"; o.Name = "bt"; o.Interval = TimeSpan.FromHours(1); o.Tags = ["country:ro", "bank"]; });
        cli.AddJob(new McpNoopJob(), o => { o.Id = "md-maib"; o.Name = "maib"; o.Group = "banks"; o.Interval = TimeSpan.FromHours(1); o.Tags = ["country:md", "bank"]; });
        cli.AddJob(new McpNoopJob(), o => { o.Id = "cleanup"; o.Name = "cleanup"; o.Interval = TimeSpan.FromHours(1); });
        cli.AddMcp();
    });

    private static async Task<JsonElement> GetAsync(HttpClient client, string url)
    {
        var res = await client.GetAsync(url);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<List<string>> IdsAsync(HttpClient client, string query) =>
        (await GetAsync(client, Jobs + query)).EnumerateArray().Select(j => j.GetProperty("id").GetString()!).Order().ToList();

    private static List<string?> Tags(JsonElement job) =>
        job.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ToList();

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, string id, string json) =>
        client.PutAsync($"{Jobs}/{id}", new StringContent(json, Encoding.UTF8, "application/json"));

    [Fact]
    public async Task JobInfo_HasNormalizedTags()
    {
        await using var h = await StartAsync();
        Assert.Equal(["country:ro", "Official"], Tags(await GetAsync(h.Client, $"{Jobs}/ro-bnr")));
        Assert.Empty(Tags(await GetAsync(h.Client, $"{Jobs}/cleanup")));
    }

    [Fact]
    public async Task List_KeepsJobsWithEveryTag_IgnoringCase()
    {
        await using var h = await StartAsync();
        Assert.Equal(["cleanup", "md-maib", "ro-bnr", "ro-bt"], await IdsAsync(h.Client, ""));
        Assert.Equal(["ro-bnr", "ro-bt"], await IdsAsync(h.Client, "?tag=COUNTRY:RO"));
        Assert.Equal(["ro-bt"], await IdsAsync(h.Client, "?tag=country:ro&tag=bank"));
        Assert.Equal(["ro-bt"], await IdsAsync(h.Client, "?tag=country:ro,bank"));
        Assert.Empty(await IdsAsync(h.Client, "?tag=missing"));
    }

    [Fact]
    public async Task Executions_FilterByTagAndGroup()
    {
        await using var h = await StartAsync();
        Assert.Equal(HttpStatusCode.OK, (await h.Client.PostAsync($"{Jobs}/ro-bt/trigger", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await h.Client.PostAsync($"{Jobs}/md-maib/trigger", null)).StatusCode);
        await Task.Delay(200);
        static List<string> JobIds(JsonElement page) =>
            page.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("jobId").GetString()!).Order().ToList();
        Assert.Equal(["md-maib", "ro-bt"], JobIds(await GetAsync(h.Client, $"{Jobs}/executions?tag=bank")));
        Assert.Equal(["md-maib"], JobIds(await GetAsync(h.Client, $"{Jobs}/executions?tag=bank&group=banks")));
        var none = await GetAsync(h.Client, $"{Jobs}/executions?tag=missing");
        Assert.Equal(0, none.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Put_ReplacesClearsAndValidatesTags()
    {
        await using var h = await StartAsync();
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(h.Client, "cleanup", """{"tags":["maintenance","Maintenance","nightly"]}""")).StatusCode);
        Assert.Equal(["maintenance", "nightly"], Tags(await GetAsync(h.Client, $"{Jobs}/cleanup")));
        Assert.Equal(["cleanup"], await IdsAsync(h.Client, "?tag=nightly"));

        await PutAsync(h.Client, "cleanup", """{"tags":null}""");
        Assert.Empty(Tags(await GetAsync(h.Client, $"{Jobs}/cleanup")));

        var tooMany = JsonSerializer.Serialize(Enumerable.Range(0, 21).Select(i => $"t{i}"));
        foreach (var bad in new[] { "\"nightly\"", """["a,b"]""", """[""]""", "[1]", $"[\"{new string('x', 65)}\"]", tooMany })
        {
            var res = await PutAsync(h.Client, "cleanup", $$"""{"tags":{{bad}}}""");
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            Assert.Contains("INVALID_VALUE", await res.Content.ReadAsStringAsync());
        }

        await PutAsync(h.Client, "cleanup", """{"tags":["kept"]}""");
        await PutAsync(h.Client, "cleanup", """{"description":"d"}""");
        Assert.Equal(["kept"], Tags(await GetAsync(h.Client, $"{Jobs}/cleanup")));
    }

    [Fact]
    public async Task McpJobsList_FiltersByTags()
    {
        await using var h = await StartAsync();
        var list = await Mcp.CallAsync(h.Client, "jobs_list", new { tags = new[] { "BANK" } });
        var ids = list.GetProperty("structuredContent").GetProperty("jobs").EnumerateArray()
            .Select(j => j.GetProperty("id").GetString()).Order().ToList();
        Assert.Equal(["md-maib", "ro-bt"], ids);
        Assert.Contains("tags: country:md, bank", Mcp.Text(list));
    }

    [Fact]
    public void Normalize_RefusesInvalidTags()
    {
        Assert.Throws<ArgumentException>(() => CliJobTags.Normalize(["a,b"]));
        Assert.Throws<ArgumentException>(() => CliJobTags.Normalize([" "]));
        Assert.Equal(["a", "B"], CliJobTags.Normalize([" a ", "A", "B"]));
        Assert.True(CliJobTags.HasAll(["Bank", "x"], ["bank"]));
        Assert.False(CliJobTags.HasAll(["Bank"], ["bank", "x"]));
    }
}
