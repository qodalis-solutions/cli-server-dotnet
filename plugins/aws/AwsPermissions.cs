using Qodalis.Cli.Abstractions;

namespace Qodalis.Cli.Plugin.Aws;

/// <summary>The permissions of the <c>aws</c> command (enforced on a protected server, along the command chain).</summary>
public static class AwsPermissions
{
    /// <summary>Every <c>aws</c> command: read the account's resources (list, describe, logs, metrics, scan, query, presigned GET URLs).</summary>
    public const string Read = "aws:read";

    /// <summary>The commands that change something: s3 cp/rm/mb/rb, ec2 start/stop/reboot, lambda invoke, sqs send/purge, sns publish, configure set.</summary>
    public const string Write = "aws:write";

    /// <summary>Added to the server's permission catalog with the module (group AWS).</summary>
    public static readonly IReadOnlyList<CliModulePermission> Entries =
    [
        new(Read, "Read AWS resources from the terminal (aws commands)", "AWS"),
        new(Write, "Change AWS resources and the AWS configuration from the terminal", "AWS"),
    ];
}
