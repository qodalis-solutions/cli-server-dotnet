# Qodalis.Cli.Plugin.Config.EfCore

Entity Framework Core store for the server's [config providers](../../README.md#config-providers). Keeps the writable settings (changed with `PATCH /api/v1/qcli/config/{provider}/{section}`, `config set` or the `config_set` MCP tool) in a database, so they survive restarts and are shared by every instance of the server. Their source is `database`.

## Install

```bash
dotnet add package Qodalis.Cli.Plugin.Config.EfCore
```

## Quick Start

```csharp
using Qodalis.Cli.Plugin.Config.EfCore;

builder.Services
    .AddControllers()
    .AddCli(cli =>
    {
        // Keep the settings in PostgreSQL (any EF Core provider works)
        cli.AddEfCoreConfigStore(o => o.UseNpgsql(connectionString));
    });
```

## The table

`QcliConfig` holds one row per provider section:

| Column | Type | |
|---|---|---|
| `Provider` | string(150) | primary key, with `Section` |
| `Section` | string(150) | |
| `Version` | long | grows by one on every write; a concurrency token |
| `Data` | text | the values changed from their defaults, a JSON object |
| `UpdatedAt` | datetime | |

It is created on first use, also inside a database that already has other tables. Change its name with the second argument (`t => t.TablePrefix = "App"`, `t.Tables["Config"] = "settings"`, `t.Schema = "cli"`), the same options as the other EF Core stores.

A change reads the row and writes it back with its version as a concurrency token: when another server changed the section meanwhile, the write is retried on the new row, and a change made against a stale version (`version` in the request, `--expected-version`) is refused with 409. Secrets are stored as given: keep the database where only the server can read it.

## Migrations and the databases plugin

A host whose own migrations create the table adds the model with `modelBuilder.ApplyConfigStorageModel()`. With the databases plugin, `AddConfigStoreSchema("main")` lists the table as the source `qcli-config` of that database.
