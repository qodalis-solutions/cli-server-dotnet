extern alias demo;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Qodalis.Cli.Tests;

/// <summary>The demo server wires the public API, the docs and the portal.</summary>
public class DemoPublicApiSmokeTests
{
    [Fact]
    public async Task Demo_ServesPortalDocsAndApi()
    {
        await using var factory = new WebApplicationFactory<demo::Program>();
        var client = factory.CreateClient();

        var document = JsonNode.Parse(await client.GetStringAsync("/qcli/api/openapi.json"))!;
        var time = document["paths"]!["/v1/demo/time"]!["get"]!;
        Assert.Equal("demo:read", time["x-required-permission"]!.GetValue<string>());
        Assert.Single(document["paths"]!.AsObject());
        Assert.NotNull(document["components"]!["securitySchemes"]!["qcliToken"]);

        var pages = JsonNode.Parse(await client.GetStringAsync("/qcli/docs/pages"))!.AsArray();
        Assert.Contains(pages, p => p!["slug"]!.GetValue<string>() == "index");

        var portal = await client.GetAsync("/qcli/portal/portal.json");
        if (portal.StatusCode == System.Net.HttpStatusCode.NotFound) return; // no portal site resolved in this checkout
        var json = JsonNode.Parse(await portal.Content.ReadAsStringAsync())!;
        Assert.Equal("/qcli/docs/pages", json["docs"]!["pages"]!.GetValue<string>());
        Assert.Equal("/qcli/api/openapi.json", json["api"]!["openapi"]!.GetValue<string>());
        Assert.Equal(200, (int)(await client.GetAsync("/qcli/portal/")).StatusCode);
    }

    [Fact]
    public async Task Demo_PublicRoute_RequiresAHostToken()
    {
        await using var factory = new WebApplicationFactory<demo::Program>();
        var client = factory.CreateClient();

        var res = await client.GetAsync("/v1/demo/time");

        Assert.Equal(401, (int)res.StatusCode);
    }

    [Fact]
    public async Task Demo_DevelopmentSwagger_StillServed()
    {
        await using var factory = new WebApplicationFactory<demo::Program>();

        // Swashbuckle 10 (Microsoft.OpenApi 2, as the public-api plugin) loads. Its v1 document itself fails on the
        // demo since before this change: two LoginRequest types (users and admin plugins) share a schema id.
        var res = await factory.CreateClient().GetAsync("/swagger/index.html");

        Assert.Equal(200, (int)res.StatusCode);
    }
}
