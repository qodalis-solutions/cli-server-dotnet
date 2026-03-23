using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;

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
    /// Credentials are resolved in order: explicit keys, named profile (credentials + config), env vars, default SDK chain.
    /// </summary>
    /// <typeparam name="T">The AWS service client type (e.g., <c>AmazonS3Client</c>).</typeparam>
    /// <param name="regionOverride">Optional region override; takes precedence over configured and environment regions.</param>
    /// <param name="profileOverride">Optional profile override; takes precedence over the configured profile.</param>
    /// <returns>A cached or newly created instance of the specified AWS service client.</returns>
    public T GetClient<T>(string? regionOverride = null, string? profileOverride = null) where T : AmazonServiceClient
    {
        var profile = profileOverride ?? _config.Profile ?? "default";

        var regionName = regionOverride
            ?? _config.Region
            ?? Environment.GetEnvironmentVariable("AWS_REGION")
            ?? Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION");

        // Update cache key to include resolved region later if needed
        var cacheKey = $"{typeof(T).Name}:{regionOverride ?? "auto"}:{profile}";

        if (_clientCache.TryGetValue(cacheKey, out var cached))
        {
            return (T)cached;
        }

        AWSCredentials? credentials = null;

        // 1. Explicit keys from config or env vars
        var accessKeyId = _config.AccessKeyId ?? Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
        var secretAccessKey = _config.SecretAccessKey ?? Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");

        if (!string.IsNullOrEmpty(accessKeyId) && !string.IsNullOrEmpty(secretAccessKey))
        {
            credentials = new BasicAWSCredentials(accessKeyId, secretAccessKey);
        }

        // 2. Named profile — try ~/.aws/credentials first, then ~/.aws/config
        if (credentials == null)
        {
            credentials = ResolveProfileCredentials(profile, ref regionName);
        }

        regionName ??= "us-east-1";
        var region = RegionEndpoint.GetBySystemName(regionName);

        T client;
        if (credentials != null)
        {
            client = (T)Activator.CreateInstance(typeof(T), credentials, region)!;
        }
        else
        {
            // 3. Fall back to default SDK credential chain
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

    private static AWSCredentials? ResolveProfileCredentials(string profile, ref string? regionName)
    {
        // CredentialProfileStoreChain searches both ~/.aws/credentials and ~/.aws/config
        var chain = new CredentialProfileStoreChain();
        if (chain.TryGetAWSCredentials(profile, out var creds))
        {
            // Pick up region from profile if not explicitly set
            if (regionName == null && chain.TryGetProfile(profile, out var p) && p.Region != null)
            {
                regionName = p.Region.SystemName;
            }
            return creds;
        }

        // Fallback: try SharedCredentialsFile directly (handles some edge cases)
        try
        {
            var sharedFile = new SharedCredentialsFile();
            if (sharedFile.TryGetProfile(profile, out var sharedProfile)
                && sharedProfile.CanCreateAWSCredentials)
            {
                if (regionName == null && sharedProfile.Region != null)
                {
                    regionName = sharedProfile.Region.SystemName;
                }
                return sharedProfile.GetAWSCredentials(sharedFile);
            }
        }
        catch
        {
            // Ignore — fall through to default chain
        }

        return null;
    }
}
