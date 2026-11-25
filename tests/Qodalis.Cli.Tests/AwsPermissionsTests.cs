using Qodalis.Cli.Abstractions;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Plugin.Aws;
using Qodalis.Cli.Security;
using Qodalis.Cli.Services;
using Qodalis.Cli.Tests.Config;
using Qodalis.Cli.Tests.Helpers;

namespace Qodalis.Cli.Tests;

/// <summary>
/// The aws plugin's permissions: aws:read on the root <c>aws</c> command (every sub-command), aws:write on the ones
/// that change something, both in the permission catalog (group AWS) once the module is added; enforced through
/// <c>POST /execute</c> on a protected server. Also <see cref="ICliModule.Permissions"/> in general (AddModule).
/// </summary>
public sealed class AwsPermissionsTests
{
    private static readonly string[] Write =
        ["configure set", "s3 cp", "s3 rm", "s3 mb", "s3 rb", "ec2 start", "ec2 stop", "ec2 reboot", "lambda invoke", "sqs send", "sqs purge", "sns publish"];

    /// <summary>Every command path under <paramref name="root"/> with its own RequiredPermissions.</summary>
    private static Dictionary<string, IEnumerable<string>?> PermissionsByPath(ICliCommandProcessor root)
    {
        var result = new Dictionary<string, IEnumerable<string>?>();
        void Walk(ICliCommandProcessor processor, string path)
        {
            foreach (var child in processor.Processors ?? [])
            {
                var childPath = path.Length == 0 ? child.Command : $"{path} {child.Command}";
                result[childPath] = child.RequiredPermissions;
                Walk(child, childPath);
            }
        }
        Walk(root, string.Empty);
        return result;
    }

    [Fact]
    public async Task AwsReadOnTheRoot_AwsWriteOnExactlyTheChangingSubCommands()
    {
        await using var h = await ConfigTestHost.StartAsync(cli => cli.AddModule(new AwsModule()));
        var root = h.Service<ICliCommandRegistry>().FindProcessor("aws")!;
        Assert.Equal([AwsPermissions.Read], root.RequiredPermissions!);
        var byPath = PermissionsByPath(root);
        foreach (var path in Write) Assert.True(byPath[path]?.SequenceEqual(["aws:write"]) == true, path);
        var others = byPath.Where(p => !Write.Contains(p.Key)).ToList();
        Assert.True(others.Count > 20, $"{others.Count} read-only paths");
        Assert.All(others, p => Assert.Null(p.Value));
    }

    [Fact]
    public async Task AddsAwsReadAndAwsWriteToTheCatalog_GroupAwsSurfaceCli_Once()
    {
        await using var h = await ConfigTestHost.StartAsync(cli => cli.AddModule(new AwsModule()).AddModule(new AwsModule()));
        var entries = h.Service<ICliPermissionCatalog>().All.Where(p => p.Group == "AWS").ToList();
        Assert.Equal(["aws:read cli", "aws:write cli"], entries.Select(p => $"{p.Name} {p.Surface}"));
        Assert.Equal(["aws:read", "aws:write"], AwsPermissions.Entries.Select(e => e.Name));
    }

    [Fact]
    public async Task AHostEntryWithTheSameName_StaysAsTheHostRegisteredIt()
    {
        await using var h = await ConfigTestHost.StartAsync(cli => cli
            .AddPermissions(p => p.Group("Host").Add("aws:read", "Mine"))
            .AddModule(new AwsModule()));
        var catalog = h.Service<ICliPermissionCatalog>();
        Assert.Equal(new CliPermission("aws:read", "Mine", "Host", CliPermissionSurface.Host), catalog.Find("AWS:READ"));
        Assert.Equal(new CliPermission("aws:write", "Change AWS resources and the AWS configuration from the terminal", "AWS", CliPermissionSurface.Cli),
            catalog.Find("aws:write"));
    }

    [Fact]
    public async Task AModuleWithoutPermissions_AddsNone()
    {
        await using var h = await ConfigTestHost.StartAsync();
        var before = h.Service<ICliPermissionCatalog>().All.Count;
        await using var h2 = await ConfigTestHost.StartAsync(cli => cli.AddModule(new WeatherTestModule()));
        Assert.Equal(before, h2.Service<ICliPermissionCatalog>().All.Count);
    }

    [Fact]
    public async Task OnAProtectedServer_AwsReadToRead_AwsWriteAsWellToChange()
    {
        await using var h = await UsersTestHost.StartAsync(o => o.MinPasswordLength = 4, configureCli: cli => cli.AddModule(new AwsModule()));
        var registry = h.Service<ICliCommandRegistry>();
        var root = await h.RootTokenAsync();
        var runner = await h.UserWithPermissionsAsync(root, "runner", "commands:execute");
        var reader = await h.UserWithPermissionsAsync(root, "reader", "commands:execute", "aws:read");
        var writer = await h.UserWithPermissionsAsync(root, "writer", "commands:execute", "aws:read", "aws:write");
        var writeOnly = await h.UserWithPermissionsAsync(root, "writeonly", "commands:execute", "aws:write");
        Task<(int Status, string Text, System.Text.Json.JsonElement Body)> Run(string line, string token) =>
            ConfigCommands.RunAsync(h.Client, registry, line, token);

        var noRead = await Run("aws configure get", runner);
        Assert.Equal(403, noRead.Status);
        Assert.Contains("aws:read", noRead.Body.GetProperty("error").GetString());
        Assert.Equal(200, (await Run("aws configure get", reader)).Status);

        var noWrite = await Run("aws configure set --region eu-north-1", reader);
        Assert.Equal(403, noWrite.Status);
        Assert.Contains("aws:write", noWrite.Body.GetProperty("error").GetString());
        Assert.Equal(403, (await Run("aws s3 rm s3://bucket/key", reader)).Status);
        Assert.Equal(403, (await Run("aws ec2 stop i-123", reader)).Status);

        // aws:write alone is not enough: the root needs aws:read
        Assert.Equal(403, (await Run("aws configure set --region eu-north-1", writeOnly)).Status);

        var ok = await Run("aws configure set --region eu-north-1", writer);
        Assert.Equal(200, ok.Status);
        Assert.Contains("AWS configuration updated.", ok.Text);
        Assert.Contains("eu-north-1", (await Run("aws configure get", writer)).Text);
    }
}
