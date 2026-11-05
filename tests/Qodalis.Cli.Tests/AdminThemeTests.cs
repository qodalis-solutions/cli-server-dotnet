using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Plugin.Admin.Extensions;
using Qodalis.Cli.Plugin.Admin.Services;
using Qodalis.Cli.Plugin.Users.Extensions;

namespace Qodalis.Cli.Tests;

public class AdminThemeTests
{
    [Theory]
    [InlineData("#fff")]
    [InlineData("#ffff")]
    [InlineData("#4f46e5")]
    [InlineData("#4f46e5cc")]
    [InlineData("rgb(1, 2, 3)")]
    [InlineData("rgba(255,255,255,0.04)")]
    [InlineData("rgb(1 2 3 / 50%)")]
    [InlineData("hsl(240deg 100% 50%)")]
    [InlineData("oklch(0.7 0.1 250)")]
    [InlineData("transparent")]
    [InlineData("rebeccapurple")]
    public void IsColor_AcceptsPlainColors(string value)
    {
        Assert.True(AdminTheme.IsColor(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#ggg")]
    [InlineData("#12345")]
    [InlineData("red;}body{display:none")]
    [InlineData("url(https://x)")]
    [InlineData("var(--x)")]
    [InlineData("rgb(1,2,3);color:red")]
    [InlineData("expression(alert(1))")]
    [InlineData("\"red\"")]
    [InlineData("rgb(calc(1),2,3)")]
    [InlineData("</style><script>")]
    public void IsColor_RejectsAnythingElse(string? value)
    {
        Assert.False(AdminTheme.IsColor(value));
        Assert.False(AdminTheme.IsColor(new string('a', 101)));
    }

    [Fact]
    public void ToJson_Default_IsDarkWithoutOverrides()
    {
        Assert.Equal("{\"mode\":\"dark\",\"dark\":{},\"light\":{}}", new AdminTheme().ToJson());
    }

    [Fact]
    public void ToJson_WritesOnlySetTokens_CamelCase_Trimmed()
    {
        var theme = new AdminTheme
        {
            Mode = AdminThemeMode.System,
            Light = new AdminThemeColors { Primary = " #2a6bba ", BackgroundAccent = "#e6eef6" },
            Dark = new AdminThemeColors { PrimaryHover = "oklch(0.7 0.1 250)" },
        };
        Assert.Equal(
            "{\"mode\":\"system\",\"dark\":{\"primaryHover\":\"oklch(0.7 0.1 250)\"},\"light\":{\"primary\":\"#2a6bba\",\"backgroundAccent\":\"#e6eef6\"}}",
            theme.ToJson());
    }

    [Fact]
    public void ToDocument_CoversEveryToken()
    {
        var colors = new AdminThemeColors();
        foreach (var prop in typeof(AdminThemeColors).GetProperties()) prop.SetValue(colors, "#000000");
        var doc = new AdminTheme { Dark = colors }.ToDocument();
        var dark = Assert.IsType<Dictionary<string, string>>(doc["dark"]);
        Assert.Equal(21, dark.Count);
        Assert.Contains("onPrimary", dark.Keys);
        Assert.Contains("controlHover", dark.Keys);
    }

    [Fact]
    public void ToDocument_RejectsCssInjection()
    {
        var theme = new AdminTheme { Light = new AdminThemeColors { Primary = "red;} body{display:none" } };
        var ex = Assert.Throws<ArgumentException>(() => theme.ToDocument());
        Assert.Contains("light.primary", ex.Message);
    }

    [Fact]
    public void ToDocument_RejectsUndefinedMode()
    {
        Assert.Throws<ArgumentException>(() => new AdminTheme { Mode = (AdminThemeMode)42 }.ToDocument());
    }

    private static async Task<IHost> StartAsync(Action<AdminConfig> configure, bool withUsers = false)
    {
        var builder = new HostBuilder().ConfigureWebHost(webHost =>
        {
            webHost.UseTestServer();
            webHost.ConfigureServices(services =>
            {
                services.AddControllers().AddCli(cli =>
                {
                    if (withUsers) cli.AddUsers(o => o.DefaultAdminPassword = "rootpass");
                    cli.AddAdmin(o =>
                    {
                        o.JwtSecret = "theme-test-secret-that-is-long-enough-for-hmac";
                        configure(o);
                    });
                });
            });
            webHost.Configure(app =>
            {
                app.UseRouting();
                if (withUsers) app.UseQodalisUsers();
                app.UseQodalisAdmin();
                app.UseEndpoints(endpoints => endpoints.MapControllers());
                app.UseCli();
            });
        });
        return await builder.StartAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThemeJson_IsServedWithoutAuthentication(bool withUsers)
    {
        using var host = await StartAsync(o =>
        {
            o.Theme.Mode = AdminThemeMode.Light;
            o.Theme.Light.Primary = "#2a6bba";
        }, withUsers);
        var res = await host.GetTestClient().GetAsync("/qcli/admin/theme.json");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/json", res.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-cache", res.Headers.CacheControl?.ToString());
        var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("light", doc.GetProperty("mode").GetString());
        Assert.Equal("#2a6bba", doc.GetProperty("light").GetProperty("primary").GetString());
        await host.StopAsync();
    }

    [Fact]
    public void Branding_Unset_IsLeftOut()
    {
        Assert.Null(new AdminBranding().ToDocument());
        Assert.Null(new AdminBranding { Name = "  ", IconUrl = "" }.ToDocument());
    }

    [Fact]
    public void Branding_WritesTheNameIconAndTerminal()
    {
        var doc = new AdminBranding { Name = " RateBell ", IconUrl = "/favicon.svg", CliName = "RateBell CLI" }.ToDocument()!;

        Assert.Equal("RateBell", doc["name"]);
        Assert.Equal("/favicon.svg", doc["icon"]);
        var cli = Assert.IsType<Dictionary<string, object>>(doc["cli"]);
        Assert.Equal("RateBell CLI", Assert.Single(cli).Value);
    }

    [Theory]
    [InlineData("/favicon.svg")]
    [InlineData("https://ratebell.app/favicon.svg")]
    [InlineData("data:image/png;base64,iVBORw0KGgo=")]
    [InlineData("data:image/svg+xml;base64,PHN2Zz48L3N2Zz4=")]
    public void Branding_AcceptsIcons(string icon)
    {
        Assert.Equal(icon, new AdminBranding { IconUrl = icon }.ToDocument()!["icon"]);
    }

    [Theory]
    [InlineData("//evil.example/x.svg")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=")]
    [InlineData("data:image/svg+xml,<svg onload=alert(1)>")]
    [InlineData("favicon.svg")]
    [InlineData("/a b.svg")]
    public void Branding_RejectsOtherIcons(string icon)
    {
        Assert.Throws<ArgumentException>(() => new AdminBranding { IconUrl = icon }.ToDocument());
    }

    [Fact]
    public void Branding_RejectsBadNames()
    {
        Assert.Throws<ArgumentException>(() => new AdminBranding { Name = new string('a', 81) }.ToDocument());
        Assert.Throws<ArgumentException>(() => new AdminBranding { CliName = "a\u0007b" }.ToDocument());
    }

    [Fact]
    public async Task ThemeJson_CarriesTheBranding()
    {
        using var host = await StartAsync(o => o.Branding = new AdminBranding { Name = "RateBell", CliName = "RateBell CLI" });
        var doc = JsonDocument.Parse(await host.GetTestClient().GetStringAsync("/qcli/admin/theme.json")).RootElement;

        Assert.Equal("dark", doc.GetProperty("mode").GetString());
        Assert.Equal("RateBell", doc.GetProperty("branding").GetProperty("name").GetString());
        Assert.Equal("RateBell CLI", doc.GetProperty("branding").GetProperty("cli").GetProperty("name").GetString());
        await host.StopAsync();
    }

    [Fact]
    public async Task InvalidBranding_FailsAtStartup()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => StartAsync(o => o.Branding.IconUrl = "javascript:alert(1)"));
    }

    [Fact]
    public async Task InvalidTheme_FailsAtStartup()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            StartAsync(o => o.Theme.Dark.Error = "url(https://evil)"));
    }
}
