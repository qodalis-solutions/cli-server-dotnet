using Amazon;
using Amazon.Runtime;

namespace Qodalis.Cli.Plugin.Aws.Services;

public class AwsCredentialManager
{
    private readonly AwsConfigService _config;
    private readonly Dictionary<string, AmazonServiceClient> _clientCache = new();

    public AwsCredentialManager(AwsConfigService config) => _config = config;

    public T GetClient<T>(string? regionOverride = null) where T : AmazonServiceClient
    {
        var regionName = regionOverride
            ?? _config.Region
            ?? Environment.GetEnvironmentVariable("AWS_REGION")
            ?? Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION")
            ?? "us-east-1";

        var cacheKey = $"{typeof(T).Name}:{regionName}:{_config.Profile ?? "default"}";

        if (_clientCache.TryGetValue(cacheKey, out var cached))
        {
            return (T)cached;
        }

        AWSCredentials? credentials = null;
        var accessKeyId = _config.AccessKeyId ?? Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
        var secretAccessKey = _config.SecretAccessKey ?? Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");

        if (!string.IsNullOrEmpty(accessKeyId) && !string.IsNullOrEmpty(secretAccessKey))
        {
            credentials = new BasicAWSCredentials(accessKeyId, secretAccessKey);
        }

        var region = RegionEndpoint.GetBySystemName(regionName);

        T client;
        if (credentials != null)
        {
            client = (T)Activator.CreateInstance(typeof(T), credentials, region)!;
        }
        else
        {
            client = (T)Activator.CreateInstance(typeof(T), region)!;
        }

        _clientCache[cacheKey] = client;
        return client;
    }

    public void ClearCache()
    {
        foreach (var client in _clientCache.Values)
        {
            client.Dispose();
        }
        _clientCache.Clear();
    }
}
