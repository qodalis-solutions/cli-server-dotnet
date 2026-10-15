using System.Text;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Tests;

/// <summary>Matching of the per-shell environment marker and parsing of <c>/proc</c> entries.</summary>
public class ShellProcessTreeTests
{
    private const string Id = "0123456789abcdef0123456789abcdef";

    private static byte[] Environ(params string[] entries) =>
        Encoding.ASCII.GetBytes(string.Join('\0', entries) + "\0");

    [Fact]
    public void NewMarker_Is128BitHex_AndUnique()
    {
        var a = ShellProcessTree.NewMarker();
        var b = ShellProcessTree.NewMarker();
        Assert.Matches("^[0-9a-f]{32}$", a);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Environ_MatchesOnlyTheExactEntry()
    {
        Assert.True(ShellProcessTree.EnvironContainsMarker(Environ("PATH=/bin", $"QCLI_SHELL_ID={Id}", "HOME=/root"), Id));
        Assert.True(ShellProcessTree.EnvironContainsMarker(Encoding.ASCII.GetBytes($"QCLI_SHELL_ID={Id}"), Id)); // no trailing NUL
        Assert.False(ShellProcessTree.EnvironContainsMarker(Environ($"QCLI_SHELL_ID={Id}0"), Id));
        Assert.False(ShellProcessTree.EnvironContainsMarker(Environ($"QCLI_SHELL_ID={Id[..31]}"), Id));
        Assert.False(ShellProcessTree.EnvironContainsMarker(Environ($"XQCLI_SHELL_ID={Id}"), Id));
        Assert.False(ShellProcessTree.EnvironContainsMarker(Environ($"OTHER=QCLI_SHELL_ID={Id}"), Id));
        Assert.False(ShellProcessTree.EnvironContainsMarker(Environ("PATH=/bin"), Id));
        Assert.False(ShellProcessTree.EnvironContainsMarker([], Id));
    }

    [Fact]
    public void Environ_RejectsEmptyOrNonHexMarkers()
    {
        Assert.False(ShellProcessTree.EnvironContainsMarker(Environ("QCLI_SHELL_ID="), ""));
        Assert.False(ShellProcessTree.EnvironContainsMarker(Environ("QCLI_SHELL_ID=x y"), "x y"));
    }

    [Fact]
    public void PsEnvironment_MatchesWholeTokensAfterThePid()
    {
        var output =
            $"  101 -bash PATH=/bin QCLI_SHELL_ID={Id} TERM=xterm\n" +
            $"  102 sleep 1004 QCLI_SHELL_ID={Id}\n" +
            $"  103 sleep 5 QCLI_SHELL_ID={Id}ff\n" +
            $"  104 sleep 5 XQCLI_SHELL_ID={Id}\n" +
            $"  105 sleep 5 QCLI_SHELL_ID=ffffffffffffffffffffffffffffffff\n" +
            "  106 sleep 5\n" +
            $"  {Id} junk\n" +
            "garbage\n";
        Assert.Equal(new HashSet<int> { 101, 102 }, ShellProcessTree.ParsePsEnvironment(output, Id));
        Assert.Empty(ShellProcessTree.ParsePsEnvironment(output, ""));
    }

    [Fact]
    public void ProcStat_ParsesFieldsAfterTheCommandName()
    {
        var e = ShellProcessTree.ParseProcStat("4242 (my (odd) cmd) S 1 4242 4242 34817 4242 4194560 0 0");
        Assert.Equal(new ShellProcessTree.ProcessEntry(4242, 1, 4242, "34817"), e);

        var noTty = ShellProcessTree.ParseProcStat("77 (sleep) S 1 77 77 0 -1 4194560");
        Assert.Equal(new ShellProcessTree.ProcessEntry(77, 1, 77, null), noTty);

        Assert.Null(ShellProcessTree.ParseProcStat("78 (defunct) Z 1 78 78 0 -1 0"));
        Assert.Null(ShellProcessTree.ParseProcStat("garbage"));
    }

    [Fact]
    public void Collect_NeverTargetsTheServerOrItsGroup()
    {
        if (OperatingSystem.IsWindows()) return;
        var targets = ShellProcessTree.Collect(Environment.ProcessId, ShellProcessTree.NewMarker());
        Assert.NotNull(targets);
        Assert.DoesNotContain(Environment.ProcessId, targets!.Pids);
    }
}
