using Amazon;
using Amazon.Runtime;

namespace Qodalis.Cli.Plugin.Aws.Services;

/// <summary>
/// Manages AWS SDK client creation and caching, resolving credentials from configuration or environment variables.
/// </summary>
public class AwsCredentialManager
{
    private readonly AwsConfigService _config;
    private readonly Dictionary<string, AmazonServiceClient> _clientCache = new();

    /// <summary>
    /// Initializes a new instance of <see cref="AwsCredentialManager"/>.
    /// </summary>
    /// <param name="config">The AWS configuration service to resolve credentials and region from.</param>
    public AwsCredentialManager(AwsConfigService config) => _config = config;

    /// <summary>
    /// Gets or creates a cached AWS service client of the specified type.
    /// Credentials are resolved from the config service, then environment variables, falling back to the default credential chain.
    /// </summary>
    /// <typeparam name="T">The AWS service client type (e.g., <c>AmazonS3Client</c>).</typeparam>
    /// <param name="regionOverride">Optional region override; takes precedence over configured and environment regions.</param>
    /// <returns>A cached or newly created instance of the specified AWS service client.</returns>
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

    /// <summary>
    /// Disposes all cached AWS service clients and clears the cache.
    /// </summary>
    public void ClearCache()
    {
        foreach (var client in _clientCache.Values)
        {
            client.Dispose();
        }
        _clientCache.Clear();
    }
}
