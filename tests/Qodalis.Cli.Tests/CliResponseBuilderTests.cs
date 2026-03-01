using Qodalis.Cli.Models;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Tests;

public class CliResponseBuilderTests
{
    [Fact]
    public void WriteText_AddsTextOutput()
    {
        var builder = new CliResponseBuilder();
        builder.WriteText("hello");

        var response = builder.Build();
        Assert.Single(response.Outputs);
        var text = Assert.IsType<TextOutput>(response.Outputs[0]);
        Assert.Equal("hello", text.Value);
        Assert.Null(text.Style);
    }

    [Fact]
    public void WriteText_WithStyle_SetsStyle()
    {
        var builder = new CliResponseBuilder();
        builder.WriteText("error!", "error");

        var text = Assert.IsType<TextOutput>(builder.Build().Outputs[0]);
        Assert.Equal("error", text.Style);
    }

    [Fact]
    public void WriteTable_AddsTableOutput()
    {
        var builder = new CliResponseBuilder();
        builder.WriteTable(["Name", "Value"], [["a", "1"]]);

        var table = Assert.IsType<TableOutput>(builder.Build().Outputs[0]);
        Assert.Equal(["Name", "Value"], table.Headers);
        Assert.Single(table.Rows);
    }

    [Fact]
    public void WriteList_AddsListOutput()
    {
        var builder = new CliResponseBuilder();
        builder.WriteList(["a", "b"]);

        var list = Assert.IsType<ListOutput>(builder.Build().Outputs[0]);
        Assert.Equal(["a", "b"], list.Items);
        Assert.False(list.Ordered ?? false);
    }

    [Fact]
    public void WriteList_Ordered()
    {
        var builder = new CliResponseBuilder();
        builder.WriteList(["x"], ordered: true);

        var list = Assert.IsType<ListOutput>(builder.Build().Outputs[0]);
        Assert.True(list.Ordered);
    }

    [Fact]
    public void WriteJson_AddsJsonOutput()
    {
        var builder = new CliResponseBuilder();
        var data = new { Foo = "bar" };
        builder.WriteJson(data);

        var json = Assert.IsType<JsonOutput>(builder.Build().Outputs[0]);
        Assert.Equal(data, json.Value);
    }

    [Fact]
    public void WriteKeyValue_AddsKeyValueOutput()
    {
        var builder = new CliResponseBuilder();
        builder.WriteKeyValue(new Dictionary<string, string> { { "k", "v" } });

        var kv = Assert.IsType<KeyValueOutput>(builder.Build().Outputs[0]);
        Assert.Single(kv.Entries);
        Assert.Equal("k", kv.Entries[0].Key);
        Assert.Equal("v", kv.Entries[0].Value);
    }

    [Fact]
    public void SetExitCode_SetsCode()
    {
        var builder = new CliResponseBuilder();
        builder.SetExitCode(42);

        Assert.Equal(42, builder.Build().ExitCode);
    }

    [Fact]
    public void DefaultExitCode_IsZero()
    {
        var builder = new CliResponseBuilder();
        Assert.Equal(0, builder.Build().ExitCode);
    }

    [Fact]
    public void Build_CombinesMultipleOutputs()
    {
        var builder = new CliResponseBuilder();
        builder.WriteText("one");
        builder.WriteText("two");
        builder.SetExitCode(0);

        var response = builder.Build();
        Assert.Equal(2, response.Outputs.Count);
    }
}
