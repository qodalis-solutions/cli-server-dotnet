using System.Text;
using Microsoft.AspNetCore.Http;
using Qodalis.Cli.Security;

namespace Qodalis.Cli.Tests;

/// <summary>The close reason of a shell refused by an authentication plugin (WebSocket close reasons are at most 123 bytes).</summary>
public class ShellCloseReasonTests
{
    private static Task<CliShellAccessResult> RefuseAsync(string message)
    {
        var context = new DefaultHttpContext();
        context.Items[CliAuthFailure.ItemKey] = new CliAuthFailure(403, message);
        return CliShellAccess.AuthorizeAsync(context, new CliShellOptions(), usersMode: true);
    }

    [Fact]
    public async Task APluginMessageIsTheReason()
    {
        var result = await RefuseAsync("forbidden: host tokens cannot use CLI routes");

        Assert.Equal(CliShellCloseCodes.Forbidden, result.CloseCode);
        Assert.Equal("forbidden: host tokens cannot use CLI routes", result.Reason);
    }

    [Fact]
    public async Task AMessageOf123BytesIsKept()
    {
        var message = new string('x', 121) + "é"; // 123 UTF-8 bytes

        Assert.Equal(message, (await RefuseAsync(message)).Reason);
    }

    [Theory]
    [InlineData(124, "")]
    [InlineData(100, "ééééééééééééé")] // 100 + 26 bytes
    public async Task ALongerMessageFallsBackToForbidden(int ascii, string tail)
    {
        var message = new string('x', ascii) + tail;
        Assert.True(Encoding.UTF8.GetByteCount(message) > 123);

        var result = await RefuseAsync(message);

        Assert.Equal(CliShellCloseCodes.Forbidden, result.CloseCode);
        Assert.Equal("forbidden", result.Reason);
    }
}
