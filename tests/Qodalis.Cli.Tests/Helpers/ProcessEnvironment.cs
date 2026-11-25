namespace Qodalis.Cli.Tests.Helpers;

/// <summary>
/// Tests that set <c>QCLI_*</c> environment variables, and tests that list them (the host config provider's
/// <c>environment</c> section), run one at a time: the process environment is shared by every test class.
/// </summary>
[CollectionDefinition(Collection)]
public sealed class ProcessEnvironment
{
    public const string Collection = "Process environment";
}
