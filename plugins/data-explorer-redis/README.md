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

## License

MIT
