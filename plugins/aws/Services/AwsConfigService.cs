namespace Qodalis.Cli.Plugin.Aws.Services;

public class AwsConfigService
{
    private string? _accessKeyId;
    private string? _secretAccessKey;
    private string? _region;
    private string _profile = "default";

    internal string? AccessKeyId => _accessKeyId;
    internal string? SecretAccessKey => _secretAccessKey;
    internal string? Region => _region;
    internal string? Profile => _profile;

    public void SetCredentials(string key, string secret)
    {
        _accessKeyId = key;
        _secretAccessKey = secret;
    }

    public void SetRegion(string region)
    {
        _region = region;
    }

    public void SetProfile(string profile)
    {
        _profile = profile;
    }

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
