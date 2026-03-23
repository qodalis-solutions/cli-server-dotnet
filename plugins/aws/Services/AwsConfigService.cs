namespace Qodalis.Cli.Plugin.Aws.Services;

/// <summary>
/// Manages AWS configuration state including credentials, region, and profile settings.
/// </summary>
public class AwsConfigService
{
    private string? _accessKeyId;
    private string? _secretAccessKey;
    private string? _region;
    private string _profile = "default";

    /// <summary>
    /// Gets the configured AWS access key ID.
    /// </summary>
    internal string? AccessKeyId => _accessKeyId;

    /// <summary>
    /// Gets the configured AWS secret access key.
    /// </summary>
    internal string? SecretAccessKey => _secretAccessKey;

    /// <summary>
    /// Gets the configured AWS region.
    /// </summary>
    internal string? Region => _region;

    /// <summary>
    /// Gets the configured AWS profile name.
    /// </summary>
    internal string? Profile => _profile;

    /// <summary>
    /// Sets the AWS access key ID and secret access key.
    /// </summary>
    /// <param name="key">The AWS access key ID.</param>
    /// <param name="secret">The AWS secret access key.</param>
    public void SetCredentials(string key, string secret)
    {
        _accessKeyId = key;
        _secretAccessKey = secret;
    }

    /// <summary>
    /// Sets the AWS region.
    /// </summary>
    /// <param name="region">The AWS region name (e.g., "us-east-1").</param>
    public void SetRegion(string region)
    {
        _region = region;
    }

    /// <summary>
    /// Sets the AWS profile name.
    /// </summary>
    /// <param name="profile">The AWS profile name from ~/.aws/credentials or ~/.aws/config.</param>
    public void SetProfile(string profile)
    {
        _profile = profile;
    }

    /// <summary>
    /// Returns a summary of the current configuration with secrets masked.
    /// </summary>
    /// <returns>A dictionary of configuration key-value pairs suitable for display.</returns>
    public Dictionary<string, string> GetConfigSummary()
    {
        return new Dictionary<string, string>
        {
            ["Access Key ID"] = _accessKeyId != null ? MaskKey(_accessKeyId) : "(not set)",
            ["Secret Access Key"] = _secretAccessKey != null ? "****" : "(not set)",
            ["Region"] = _region ?? "(not set)",
            ["Profile"] = _profile,
        };
    }

    private static string MaskKey(string key)
    {
        if (key.Length <= 8) return "****";
        return key[..4] + "***" + key[^5..];
    }
}
