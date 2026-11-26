# Qodalis.Cli.Plugin.DataExplorer.Redis

Redis data explorer provider for [Qodalis CLI Server (.NET)](https://github.com/qodalis-solutions/cli-server-dotnet). Enables interacting with Redis through the Data Explorer UI using a curated set of safe Redis commands, with results normalized into tabular format.

## Install

```bash
dotnet add package Qodalis.Cli.Plugin.DataExplorer.Redis
```

## Quick Start

```csharp
using Qodalis.Cli.Plugin.DataExplorer.Redis;

builder.Services.AddControllers().AddCli(cli =>
{
    cli.AddDataExplorerRedis("localhost:6379", o =>
    {
        o.Name = "my-redis";
        o.Description = "Production Redis";
    });
});
```

## Allowed Commands

Only the following Redis commands are permitted:

| Category | Commands |
|---|---|
| Strings | `GET`, `SET`, `DEL`, `MGET`, `MSET`, `APPEND`, `STRLEN`, `INCR`, `DECR`, `INCRBY`, `DECRBY` |
| Keys | `KEYS`, `EXISTS`, `TYPE`, `TTL`, `PTTL`, `EXPIRE`, `SCAN` |
| Hashes | `HGET`, `HSET`, `HGETALL`, `HMGET`, `HMSET`, `HDEL`, `HKEYS`, `HVALS`, `HLEN`, `HSCAN` |
| Lists | `LRANGE`, `LLEN`, `LINDEX`, `LPUSH`, `RPUSH`, `LPOP`, `RPOP` |
| Sets | `SMEMBERS`, `SCARD`, `SISMEMBER`, `SADD`, `SREM`, `SSCAN` |
| Sorted Sets | `ZADD`, `ZRANGE`, `ZRANGEBYSCORE`, `ZCARD`, `ZSCORE`, `ZREM`, `ZSCAN` |
| Server | `INFO`, `DBSIZE`, `SELECT`, `PING` |

## Dependencies

- `StackExchange.Redis`

## Terminal commands

None of its own: the provider is queried through the [data explorer](../../README.md#data-explorer-sources)
(`data-explorer` in the web CLI).

## Install from the dashboard or in code

The package declares an entry point (`RedisPluginEntry`, plugin id `data-explorer-redis`), so it can be installed from the dashboard's
marketplace (the admin plugin's server plugins, loaded at the next start) or added in code through the same entry:

```csharp
cli.AddPlugin<RedisPluginEntry>(new { name = "app" });
```

Its settings (config provider `data-explorer-redis` unless noted; code options are their defaults, saved settings override them):
`name`, `description`, `connectionString` (required secret), `readOnly` (default true), `timeoutMs`, `maxRows`; order Source (70), after the data explorer entry; no source is added until it is configured. A required setting without a value lists the plugin as `needs-configuration` with `missing`; see
[Server plugins](../../README.md#server-plugins-from-code-or-the-dashboard).

## License

MIT
