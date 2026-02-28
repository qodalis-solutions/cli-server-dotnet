using System.Security.Cryptography;
using System.Text;
using Qodalis.Cli.Abstractions;

namespace Qodalis.Cli.Demo.Processors;

public class CliHashCommandProcessor : CliCommandProcessor
{
    private static readonly string[] SupportedAlgorithms = ["md5", "sha1", "sha256", "sha512"];

    public override string Command { get; set; } = "hash";
    public override string Description { get; set; } = "Computes hash of the input text";

    public override IEnumerable<ICliCommandParameterDescriptor>? Parameters { get; set; } =
    [
        new CliCommandParameterDescriptor
        {
            Name = "algorithm",
            Aliases = ["-a"],
            Description = "Hash algorithm (md5, sha1, sha256, sha512)",
            Type = CommandParameterType.String,
            DefaultValue = "sha256",
        },
    ];

    public override Task<string> HandleAsync(CliProcessCommand command, CancellationToken cancellationToken = default)
    {
        var text = command.Value;
        if (string.IsNullOrWhiteSpace(text))
            return Task.FromResult("Usage: hash <text> [--algorithm sha256]");

        var algo = command.Args.TryGetValue("algorithm", out var a) ? a.ToString()!.ToLower() : "sha256";
        if (!SupportedAlgorithms.Contains(algo))
            return Task.FromResult($"Unsupported algorithm: {algo}. Supported: {string.Join(", ", SupportedAlgorithms)}");

        var bytes = Encoding.UTF8.GetBytes(text);
        byte[] hash = algo switch
        {
            "md5" => MD5.HashData(bytes),
            "sha1" => SHA1.HashData(bytes),
            "sha256" => SHA256.HashData(bytes),
            "sha512" => SHA512.HashData(bytes),
            _ => throw new InvalidOperationException($"Unsupported algorithm: {algo}"),
        };

        var hex = Convert.ToHexString(hash).ToLower();
        return Task.FromResult($"{algo}: {hex}");
    }
}
