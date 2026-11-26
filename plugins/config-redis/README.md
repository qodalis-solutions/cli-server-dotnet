# Qodalis.Cli.Plugin.Config.Redis

A Redis store for the [config providers](../../README.md#config-providers) of
[Qodalis CLI Server (.NET)](https://github.com/qodalis-solutions/cli-server-dotnet): the writable settings of one
provider, several or all of them, kept in Redis and shared by every server using the same Redis.

## Install

```bash
dotnet add package Qodalis.Cli.Plugin.Config.Redis
```

It uses [StackExchange.Redis](https://github.com/StackExchange/StackExchange.Redis) (installed with it).

## Quick Start

Every provider can have its own store. Keep `jobs` in Redis and the rest in the app's settings file:

```csharp
using Qodalis.Cli.Plugin.Config.Redis;

builder.Services
    .AddControllers()
    .AddCli(cli =>
    {
        cli.SetAppSettingsConfigStore();                                           // the default store: appsettings.json, section Qcli:Config
        cli.AddRedisConfigStore(o => o.Url = "redis://cache:6379/0", providerId: "jobs"); // jobs in Redis
    });
```

Other ways to give a provider this store:

```csharp
cli.AddRedisConfigStore(o => o.Url = redisUrl);                 // the default store: every provider without its own
cli.SetConfigStore<RedisConfigStore>("users");                  // one more provider, by id (the same instance)
cli.SetConfigStore(new RedisConfigStore(new() { Url = redisUrl }), "portal");
cli.ConfigureJobs(o => o.ConfigStore = redisStore);              // the jobs plugin's provider (also users, portal, admin logs)
new CliStoredConfigProvider(new() { Id = "billing", Title = "Billing", Store = redisStore, Sections = [/* ... */] });
```

One store may serve several providers: their data never mixes (one key per provider and section). The host's store for
an id wins over the provider's own store.

## Options

| Option | Default | Description |
|---|---|---|
| `Url` | | A Redis URL (`redis://[[user]:password@]host:6379/0`, `rediss://` for TLS) or a StackExchange.Redis configuration string; the store connects on first use (or when the server's config is made ready) and closes the connection when disposed |
| `Connection` | the container's `IConnectionMultiplexer` | A connection of your app; the store never closes it. `Url`, `Connection` (or one in the container) or `Client` is required |
| `Database` | the connection's | The database of `Connection` (a `Url` names its own) |
| `Client` | | An `IRedisConfigClient` running the commands (tests, a custom client) |
| `KeyPrefix` | `qcli:config:` | Prepended to every key |
| `TtlSeconds` | none | Expire a section this many seconds after its last change; without it sections are kept until deleted |
| `MaxRetries` | `10` | How often a write without an expected version is retried when another write came first |

## How It Works

- **One hash per provider section**: `qcli:config:<provider>:<section>` with the fields `values` (the settings
  changed from their defaults, as JSON), `version` (grows by one on every write) and `updatedAt` (ISO 8601).
- **Atomic, versioned writes**: a write reads the section, applies the changes (`null` removes a key) and stores them
  with a Lua script (`RedisConfigStore.WriteScript`, the same as the Node and Python servers) that first checks the
  version is still the one read. A change made with an expected version (the dashboard, `config set --expected-version`,
  `PATCH` with `version`) that is stale, or that loses the race to another server, is refused with
  `CliConfigConflictException` (HTTP 409 with the current version); one without is retried on the new values, so
  concurrent writers never lose a change.
- **Ready**: `ReadyAsync` connects; the server's config ready (and every config request) fails while Redis cannot be
  reached.
- **Source** `database`, **persistent**: the settings survive server restarts (as long as Redis keeps its data).
- **`Describe()`**: `{ "kind": "redis", "title": "Redis", "detail": "cache:6379/0, prefix qcli:config", "persistent": true }`
  (host, port, database and key prefix; never the user or password), shown as the provider's `store` by
  `GET /api/v1/qcli/config` and by `config list`:

```text
jobs  Jobs  · Redis (cache:6379/0, prefix qcli:config)
```

Secrets are stored as given: protect the Redis (password, TLS, network) like the rest of the server's data.

## Tests

The store runs the same contract as the in-memory, JSON file and database stores on a fake Redis. Set
`QCLI_TEST_REDIS` (`redis://localhost:6379/15`) to run it against a real Redis too, keys under a temporary prefix
removed afterwards.

## Terminal commands

None of its own: the settings are read and changed with the server's [`config` command](../../README.md#terminal-commands)
(`config list`, `config get`, `config set`, `config reset`).

## Install from the dashboard or in code

The package declares an entry point (`RedisConfigPluginEntry`, plugin id `config-redis`), so it can be installed from the dashboard's
marketplace (the admin plugin's server plugins, loaded at the next start) or added in code through the same entry:

```csharp
cli.AddPlugin<RedisConfigPluginEntry>(new { url = "redis://cache:6379/0" });
```

Its settings (config provider `config-redis` unless noted; code options are their defaults, saved settings override them):
`url` (required secret), `keyPrefix`, `ttlSeconds`; order ConfigStore (20): Redis becomes the default config store; its own settings stay in `plugin-settings.json`, never in Redis. A required setting without a value lists the plugin as `needs-configuration` with `missing`; see
[Server plugins](../../README.md#server-plugins-from-code-or-the-dashboard).
