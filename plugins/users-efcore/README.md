# Qodalis.Cli.Plugin.Users.EfCore

Entity Framework Core store for the [users plugin](../users/README.md)'s API tokens. Keeps the tokens (`token create`, the dashboard's Tokens page, and the tokens of terminals linked with `server add`) in a database, so they survive restarts and are shared by every instance of the server. Only each token's SHA-256 is stored.

## Install

```bash
dotnet add package Qodalis.Cli.Plugin.Users.EfCore
```

## Quick Start

```csharp
using Qodalis.Cli.Plugin.Users.EfCore;

builder.Services
    .AddControllers()
    .AddCli(cli =>
    {
        cli.AddUsers();

        // Keep API tokens in PostgreSQL (any EF Core provider works)
        cli.AddEfCoreApiTokenStore(o => o.UseNpgsql(connectionString));
    });
```

The `api_tokens` table is created on first use, also inside a database that already has other tables. Expired tokens are deleted when tokens are listed (they are refused anyway).

## Hosts with their own sign-in

A host that signs users in itself (`UseExternalAuth`) turns tokens on with `AllowApiTokens()`; then its users create tokens and approve terminal links, and the plugin checks each token's user in the host's user store on every use:

```csharp
cli.AddUsers(o => o.UseExternalAuth(MyAuth.AuthenticateAsync).SetUserStore<MyUserStore>().AllowApiTokens());
cli.AddEfCoreApiTokenStore(o => o.UseNpgsql(connectionString));
```

## Host migrations

A host that manages its database with EF Core migrations can create the table in its own migrations instead:

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.ApplyApiTokenStorageModel();
}
```

## License

MIT
