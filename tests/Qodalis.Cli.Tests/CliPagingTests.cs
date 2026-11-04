using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Abstractions.Helpers;
using Qodalis.Cli.Mcp;
using Qodalis.Cli.Models;
using Qodalis.Cli.Security;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Tests;

public class CliPagingTests
{
    private static CliProcessCommand Command(params (string Name, object Value)[] args) =>
        new() { Command = "list", Args = args.ToDictionary(a => a.Name, a => a.Value) };

    private static string[][] Rows(int count) =>
        [.. Enumerable.Range(1, count).Select(i => new[] { i.ToString() })];

    [Fact]
    public void From_ReadsSkipAndTake_WithDefaultsAndLimits()
    {
        Assert.Equal(new CliPageRequest(0, 50), CliPaging.From(Command()));
        Assert.Equal(new CliPageRequest(40, 20), CliPaging.From(Command(("skip", 40), ("take", "20"))));
        Assert.Equal(new CliPageRequest(0, 10), CliPaging.From(Command(("skip", "-5"), ("take", 0)), defaultTake: 10));
        Assert.Equal(new CliPageRequest(0, 50), CliPaging.From(Command(("skip", "abc"), ("take", "2.5"))));
        Assert.Equal(CliPaging.MaxTake, CliPaging.From(Command(("take", 100000))).Take);

        // The browser's JSON arrives as JsonElement
        var args = JsonSerializer.Deserialize<Dictionary<string, object>>("""{"skip":100,"take":"25"}""")!;
        Assert.Equal(new CliPageRequest(100, 25), CliPaging.From(new CliProcessCommand { Command = "list", Args = args }));
    }

    [Fact]
    public void Parameters_DeclareSkipAndTake()
    {
        Assert.Equal(["skip", "take"], CliPaging.Parameters().Select(p => p.Name));
        Assert.Equal(25, CliPaging.Parameters(25)[1].DefaultValue);
    }

    [Fact]
    public void WritePagedTable_WorksOutHasMoreFromTotal()
    {
        var builder = new CliResponseBuilder();
        builder.WritePagedTable(["N"], Rows(50), new CliPageRequest(0, 50), total: 120);
        builder.WritePagedTable(["N"], Rows(20), new CliPageRequest(100, 50), total: 120);
        builder.WritePagedTable(["N"], Rows(50), new CliPageRequest(0, 50), hasMore: true);

        var pages = builder.Build().Outputs.Cast<TableOutput>().Select(t => t.Page!).ToList();
        Assert.True(pages[0].HasMore);
        Assert.False(pages[1].HasMore);
        Assert.True(pages[2].HasMore);
        Assert.Null(pages[2].Total);
    }

    [Fact]
    public void TablePage_SerializesCamelCase_WithoutNulls()
    {
        var builder = new CliResponseBuilder();
        builder.WritePagedTable(["N"], Rows(1), new CliPageRequest(5, 1), hasMore: false);
        builder.WriteTable(["N"], Rows(1));

        var json = JsonSerializer.Serialize(builder.Build().Outputs, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        Assert.Contains("\"page\":{\"skip\":5,\"take\":1,\"hasMore\":false}", json);
        Assert.Single(json.Split("\"page\":{").Skip(1));
    }

    [Fact]
    public async Task Executor_CutsUnpagedTables_AtMaxTableRows()
    {
        var registry = new CliCommandRegistry(NullLogger<CliCommandRegistry>.Instance);
        registry.Register(new TableProcessor("big", b => b.WriteTable(["N"], Rows(300))));
        registry.Register(new TableProcessor("paged", b => b.WritePagedTable(["N"], Rows(300), new CliPageRequest(0, 300), total: 900)));
        registry.Register(new TableProcessor("small", b => b.WriteTable(["N"], Rows(5))));

        var executor = new CliCommandExecutorService(registry, NullLogger<CliCommandExecutorService>.Instance, [], new CliServerOptions());

        var big = Assert.IsType<TableOutput>((await executor.ExecuteAsync(new CliProcessCommand { Command = "big" })).Outputs[0]);
        Assert.Equal(200, big.Rows.Length);
        Assert.Equal(300, big.Page!.Total);
        Assert.True(big.Page.Truncated);
        Assert.True(big.Page.HasMore);

        var paged = Assert.IsType<TableOutput>((await executor.ExecuteAsync(new CliProcessCommand { Command = "paged" })).Outputs[0]);
        Assert.Equal(300, paged.Rows.Length);
        Assert.Null(paged.Page!.Truncated);

        var small = Assert.IsType<TableOutput>((await executor.ExecuteAsync(new CliProcessCommand { Command = "small" })).Outputs[0]);
        Assert.Null(small.Page);

        var unlimited = new CliCommandExecutorService(registry, NullLogger<CliCommandExecutorService>.Instance, [], new CliServerOptions { MaxTableRows = 0 });
        var all = Assert.IsType<TableOutput>((await unlimited.ExecuteAsync(new CliProcessCommand { Command = "big" })).Outputs[0]);
        Assert.Equal(300, all.Rows.Length);
    }

    [Fact]
    public void McpText_DescribesTheRows()
    {
        Assert.Equal("(rows 51-100 of 120; next: skip=100)",
            CliMcpServer.DescribePage(new CliTablePage { Skip = 50, Take = 50, Total = 120, HasMore = true }, 50));
        Assert.Equal("(rows 51-100; next: skip=100)",
            CliMcpServer.DescribePage(new CliTablePage { Skip = 50, Take = 50, HasMore = true }, 50));
        Assert.Equal("(rows 101-120 of 120)",
            CliMcpServer.DescribePage(new CliTablePage { Skip = 100, Take = 50, Total = 120, HasMore = false }, 20));
        Assert.Equal("(no rows at skip=200 of 120)",
            CliMcpServer.DescribePage(new CliTablePage { Skip = 200, Take = 50, Total = 120, HasMore = false }, 0));
        Assert.Equal("(first 200 of 300 rows; the command does not page, narrow it down to see the rest)",
            CliMcpServer.DescribePage(new CliTablePage { Skip = 0, Take = 200, Total = 300, HasMore = true, Truncated = true }, 200));
    }

    private sealed class TableProcessor(string command, Action<CliResponseBuilder> write) : CliCommandProcessor, ICliCommandProcessor
    {
        public override string Command { get; set; } = command;
        public override string Description { get; set; } = command;

        public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default) =>
            Task.FromResult("");

        public Task<ICliStructuredResponse?> HandleStructuredAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
        {
            var builder = new CliResponseBuilder();
            write(builder);
            return Task.FromResult<ICliStructuredResponse?>(builder.Build());
        }
    }
}
