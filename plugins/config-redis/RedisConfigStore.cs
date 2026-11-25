using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Qodalis.Cli.Abstractions.Config;
using StackExchange.Redis;

namespace Qodalis.Cli.Plugin.Config.Redis;

/// <summary>The Redis commands <see cref="RedisConfigStore"/> uses; <see cref="RedisConfigClient"/> runs them on StackExchange.Redis.</summary>
public interface IRedisConfigClient
{
    /// <summary>Where it connects, as <c>host:port/db</c> (or a socket path); never the password or user.</summary>
    string Where { get; }

    /// <summary>Connects (once); <see cref="RedisConfigStore.ReadyAsync"/> waits for it.</summary>
    Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary><c>HGETALL key</c>: the hash's fields, empty when the key is missing.</summary>
    Task<IReadOnlyDictionary<string, string>> HashGetAllAsync(string key);

    /// <summary><c>EVAL script 1 key args...</c>.</summary>
    Task<RedisResult> EvaluateAsync(string script, string key, RedisValue[] args);
}

/// <summary>Options of <see cref="RedisConfigStore"/>: a <see cref="Url"/>, a shared <see cref="Connection"/> or a <see cref="Client"/>.</summary>
public sealed class RedisConfigStoreOptions
{
    /// <summary>
    /// A Redis URL (<c>redis://[[user]:password@]host:6379/0</c>, <c>rediss://</c> for TLS) or a StackExchange.Redis
    /// configuration string (<c>cache:6379,password=...,defaultDatabase=0</c>): the store connects on first use (or
    /// when the server's config is made ready) and closes the connection when disposed.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>A connection of the app, shared; the store never closes it (resolved from the container when neither this nor <see cref="Url"/> is set).</summary>
    public IConnectionMultiplexer? Connection { get; set; }

    /// <summary>The database of <see cref="Connection"/> (default its configured one, else 0); a <see cref="Url"/> names its own.</summary>
    public int? Database { get; set; }

    /// <summary>The commands as another client runs them (tests, a custom client); wins over <see cref="Url"/> and <see cref="Connection"/>.</summary>
    public IRedisConfigClient? Client { get; set; }

    /// <summary>Prepended to every key (default <c>qcli:config:</c>): <c>qcli:config:&lt;provider&gt;:&lt;section&gt;</c>.</summary>
    public string KeyPrefix { get; set; } = RedisConfigStore.DefaultKeyPrefix;

    /// <summary>Expire a section this many seconds after its last change (default none: kept until deleted).</summary>
    public int? TtlSeconds { get; set; }

    /// <summary>How often a write without an expected version is retried when another write came first (default 10).</summary>
    public int MaxRetries { get; set; } = 10;
}

/// <summary>
/// Keeps writable config settings in Redis (source <c>database</c>, kind <c>redis</c>): one hash per provider section
/// (<c>qcli:config:&lt;provider&gt;:&lt;section&gt;</c>) with the fields <c>values</c> (JSON), <c>version</c> and
/// <c>updatedAt</c>. A write reads the section, applies the changes and stores them with a Lua script
/// (<see cref="WriteScript"/>) that checks the version first, so servers sharing the Redis never lose a change: a stale
/// expected version is refused (<see cref="CliConfigConflictException"/>, 409), a write without one is retried.
/// Use it for every provider, or for some only, next to other stores:
/// <code>
/// cli.SetConfigStore(new JsonFileConfigStore("qcli-config.json"));                     // the default store
/// cli.AddRedisConfigStore(o =&gt; o.Url = "redis://cache:6379/0", providerId: "jobs");   // jobs in Redis
/// </code>
/// </summary>
public sealed class RedisConfigStore : ICliConfigStore, IAsyncDisposable, IDisposable
{
    /// <summary>The key prefix when none is given.</summary>
    public const string DefaultKeyPrefix = "qcli:config:";

    /// <summary>
    /// The atomic write: sets the section's values and the next version only when the stored version is still the one
    /// the change was computed from (<c>ARGV[1]</c>); answers <c>{1, newVersion}</c>, or <c>{0, currentVersion}</c> when
    /// another write came first. <c>ARGV[2]</c> the values as JSON, <c>ARGV[3]</c> the time (ISO 8601), <c>ARGV[4]</c>
    /// the TTL in seconds (0: none). The same script as the Node and Python servers.
    /// </summary>
    public const string WriteScript = """

        local current = tonumber(redis.call('HGET', KEYS[1], 'version') or '0') or 0
        if current ~= tonumber(ARGV[1]) then
            return {0, current}
        end
        local nextVersion = current + 1
        redis.call('HSET', KEYS[1], 'version', nextVersion, 'values', ARGV[2], 'updatedAt', ARGV[3])
        local ttl = tonumber(ARGV[4]) or 0
        if ttl > 0 then
            redis.call('EXPIRE', KEYS[1], ttl)
        end
        return {1, nextVersion}

        """;

    private readonly int _ttlSeconds;
    private readonly int _maxRetries;
    private readonly TimeProvider _time;

    public RedisConfigStore(RedisConfigStoreOptions options, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Client != null) Client = options.Client;
        else if (!string.IsNullOrWhiteSpace(options.Url)) Client = RedisConfigClient.FromUrl(options.Url);
        else if (options.Connection != null) Client = new RedisConfigClient(options.Connection, options.Database);
        else throw new ArgumentException("RedisConfigStore needs a Url, a Connection or a Client");
        if (options.TtlSeconds is < 0) throw new ArgumentException("TtlSeconds must be a whole number of seconds, 0 or more");
        KeyPrefix = options.KeyPrefix ?? DefaultKeyPrefix;
        _ttlSeconds = options.TtlSeconds ?? 0;
        _maxRetries = Math.Max(0, options.MaxRetries);
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The client running the commands.</summary>
    public IRedisConfigClient Client { get; }

    /// <summary>Prepended to every key.</summary>
    public string KeyPrefix { get; }

    public string Source => CliConfigSources.Database;

    /// <summary><c>cache:6379/0, prefix qcli:config</c>: host, port, database and key prefix; never the password or user.</summary>
    public CliConfigStoreDescription? Describe()
    {
        var prefix = KeyPrefix.TrimEnd(':');
        return new CliConfigStoreDescription
        {
            Kind = CliConfigStoreKinds.Redis,
            Title = "Redis",
            Detail = $"{Client.Where}{(prefix.Length > 0 ? $", prefix {prefix}" : "")}",
            Persistent = true,
        };
    }

    /// <summary>Connects to Redis (once): config ready fails when it cannot.</summary>
    public Task ReadyAsync(CancellationToken cancellationToken = default) => Client.ConnectAsync(cancellationToken);

    /// <summary>The section's key: <c>&lt;prefix&gt;&lt;provider&gt;:&lt;section&gt;</c>.</summary>
    public string KeyOf(string provider, string section) => $"{KeyPrefix}{provider}:{section}";

    public async Task<CliConfigStoredSection> ReadAsync(string provider, string section, CancellationToken cancellationToken = default) =>
        Parse(await Client.HashGetAllAsync(KeyOf(provider, section)));

    public async Task<CliConfigStoredSection> WriteAsync(string provider, string section, IReadOnlyDictionary<string, JsonNode?> changes,
        long? expectedVersion = null, CancellationToken cancellationToken = default)
    {
        var key = KeyOf(provider, section);
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = await ReadAsync(provider, section, cancellationToken);
            if (expectedVersion != null && expectedVersion != current.Version)
                throw new CliConfigConflictException(current.Version.ToString(CultureInfo.InvariantCulture));
            var values = CliConfigStoreHelpers.ApplyChanges(current.Values, changes);
            var json = new CliConfigStoredSection(values, 0).ValuesToJson().ToJsonString();
            var reply = await Client.EvaluateAsync(WriteScript, key,
            [
                current.Version,
                json,
                _time.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
                _ttlSeconds,
            ]);
            var (ok, version) = ReadReply(reply);
            if (ok == 1) return new CliConfigStoredSection(values, version).Clone();
            // Another write came first
            if (expectedVersion != null) throw new CliConfigConflictException(version.ToString(CultureInfo.InvariantCulture));
            if (attempt >= _maxRetries)
                throw new InvalidOperationException($"config section {provider}.{section}: too many concurrent writes");
        }
    }

    private static (long Ok, long Version) ReadReply(RedisResult reply)
    {
        try
        {
            if (reply.Resp2Type == ResultType.Array && (RedisResult[]?)reply is { Length: 2 } items)
            {
                var ok = (long)items[0];
                if (ok is 0 or 1) return (ok, (long)items[1]);
            }
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
        {
            // reported below
        }
        throw new InvalidOperationException($"unexpected reply from Redis: {reply}");
    }

    /// <summary>A section from its hash: empty at version 0 when missing; unreadable values or versions read as empty / 0.</summary>
    private static CliConfigStoredSection Parse(IReadOnlyDictionary<string, string>? hash)
    {
        if (hash == null || hash.Count == 0) return CliConfigStoredSection.Empty;
        Dictionary<string, JsonNode?> values;
        try
        {
            values = CliConfigStoredSection.ValuesFromJson(hash.TryGetValue("values", out var text) ? JsonNode.Parse(text) : null);
        }
        catch (JsonException)
        {
            values = new Dictionary<string, JsonNode?>();
        }
        var version = hash.TryGetValue("version", out var v) && long.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
        return new CliConfigStoredSection(values, version);
    }

    /// <summary>Ends the connection the store opened from a URL; a connection or client given to it is left open.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Client is RedisConfigClient own) await own.DisposeAsync();
    }

    public void Dispose()
    {
        if (Client is RedisConfigClient own) own.Dispose();
    }
}

/// <summary>Runs the store's commands on StackExchange.Redis: a shared <see cref="IConnectionMultiplexer"/>, or one it opens from a URL.</summary>
public sealed class RedisConfigClient : IRedisConfigClient, IAsyncDisposable, IDisposable
{
    private readonly ConfigurationOptions? _configuration;
    private readonly int? _database;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private IConnectionMultiplexer? _connection;
    private readonly bool _ownsConnection;

    /// <summary>Uses <paramref name="connection"/> (never closed by the store), database <paramref name="database"/> (default the connection's).</summary>
    public RedisConfigClient(IConnectionMultiplexer connection, int? database = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _database = database;
        var endPoint = connection.GetEndPoints().FirstOrDefault();
        Where = $"{EndPointText(endPoint)}/{database ?? 0}";
    }

    private RedisConfigClient(ConfigurationOptions configuration)
    {
        _configuration = configuration;
        _ownsConnection = true;
        _database = configuration.DefaultDatabase;
        Where = $"{EndPointText(configuration.EndPoints.FirstOrDefault())}/{configuration.DefaultDatabase ?? 0}";
    }

    /// <summary>A client connecting (on first use) to a <c>redis://</c> / <c>rediss://</c> URL or a StackExchange.Redis configuration string.</summary>
    public static RedisConfigClient FromUrl(string url) => new(ParseUrl(url));

    public string Where { get; }

    /// <summary>
    /// <c>redis://[[user]:password@]host[:port][/db]</c> (<c>rediss://</c>: TLS) as StackExchange.Redis options; anything
    /// else is read as a configuration string. Connecting does not abort on failure (it retries in the background).
    /// </summary>
    public static ConfigurationOptions ParseUrl(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ConfigurationOptions options;
        if (url.StartsWith("redis://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("rediss://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(url);
            options = new ConfigurationOptions { Ssl = uri.Scheme.Equals("rediss", StringComparison.OrdinalIgnoreCase) };
            options.EndPoints.Add(uri.Host, uri.Port > 0 ? uri.Port : 6379);
            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                var parts = uri.UserInfo.Split(':', 2);
                var user = Uri.UnescapeDataString(parts[0]);
                if (parts.Length == 2)
                {
                    if (user.Length > 0) options.User = user;
                    options.Password = Uri.UnescapeDataString(parts[1]);
                }
                else if (user.Length > 0) options.Password = user;
            }
            var path = uri.AbsolutePath.Trim('/');
            if (path.Length > 0)
            {
                if (!int.TryParse(path, NumberStyles.None, CultureInfo.InvariantCulture, out var db))
                    throw new ArgumentException("invalid Redis URL: the path must be the database number");
                options.DefaultDatabase = db;
            }
        }
        else
        {
            options = ConfigurationOptions.Parse(url);
        }
        options.AbortOnConnectFail = false;
        return options;
    }

    private static string EndPointText(EndPoint? endPoint) => endPoint switch
    {
        DnsEndPoint dns => $"{dns.Host}:{(dns.Port > 0 ? dns.Port : 6379)}",
        IPEndPoint ip => ip.ToString(),
        System.Net.Sockets.UnixDomainSocketEndPoint unix => unix.ToString(),
        null => "localhost:6379",
        _ => endPoint.ToString() ?? "localhost:6379",
    };

    public async Task ConnectAsync(CancellationToken cancellationToken = default) => await ConnectionAsync(cancellationToken);

    private async Task<IConnectionMultiplexer> ConnectionAsync(CancellationToken cancellationToken = default)
    {
        if (_connection != null) return _connection;
        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            return _connection ??= await ConnectionMultiplexer.ConnectAsync(_configuration!);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async Task<IDatabase> DatabaseAsync() => (await ConnectionAsync()).GetDatabase(_database ?? -1);

    public async Task<IReadOnlyDictionary<string, string>> HashGetAllAsync(string key)
    {
        var entries = await (await DatabaseAsync()).HashGetAllAsync(key);
        return entries.ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());
    }

    public async Task<RedisResult> EvaluateAsync(string script, string key, RedisValue[] args) =>
        await (await DatabaseAsync()).ScriptEvaluateAsync(script, [key], args);

    public async ValueTask DisposeAsync()
    {
        if (_ownsConnection && _connection != null) await _connection.DisposeAsync();
    }

    public void Dispose()
    {
        if (_ownsConnection) _connection?.Dispose();
    }
}
