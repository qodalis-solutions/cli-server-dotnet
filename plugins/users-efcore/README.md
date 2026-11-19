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

The `api_tokens` table is created on first use, also inside a database that already has other tables. Expired tokens are deleted when tokens are listed (they are refused anyway). A table created by 1.0.0-beta.28 or older gets the nullable `Audience` column on first use (see [Upgrading](#upgrading-from-100-beta28-or-older-the-audience-column)); its rows keep `NULL`, read as `cli`.

Token-name uniqueness (per user, case-insensitive) and the limit of 20 tokens per user are enforced in process, not by the database. Two services sharing one `api_tokens` table can each pass the check at the same moment, so a user could end up with two tokens of the same name, or 21 tokens. A host that creates tokens from more than one service should create them from one of them, or add a unique index on `(UserId, Name)` in its own migration (the name check is case-insensitive, so use a case-insensitive collation where the database supports one). Several services may *validate* tokens over the shared table (for example a guard-only service that serves the public API: `o.UseGuardOnly()` in the users plugin); tokens must be *created and revoked* from one service only.

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

### Upgrading from 1.0.0-beta.28 or older (the `Audience` column)

By default the store adds the nullable `Audience` column to an existing `api_tokens` table the first time it is used (`ALTER TABLE ... ADD`, with the column type the model gives the provider: `nvarchar(16)` on SQL Server, `character varying(16)` on PostgreSQL). If the app starts before the host's own migration runs, the store adds the column first. A plain `AddColumn` migration then fails with "column already exists". A host whose migrations own the table therefore picks one of these:

1. **Turn the automatic upgrade off and add the column in a migration** (recommended; works on every provider, including SQLite):

   ```csharp
   cli.AddEfCoreApiTokenStore(o => o.UseSqlServer(connectionString), o => o.UpgradeSchema = false);
   ```

   Then run `dotnet ef migrations add QcliApiTokenAudience`. Safe order: apply the migration (`dotnet ef database update`, or `Database.Migrate()` at startup before requests are served), then let the app serve requests. Until the column exists, the store refuses to work: every token operation throws an `InvalidOperationException` that names the missing `Audience` column and `UpgradeSchema`. Tokens are not read with a guessed audience. Once the migration has run, the same process recovers on the next call.

   `UpgradeSchema = false` only stops the store from altering an existing table: a **missing** `api_tokens` table is still created on first use (with every column), and the host's own migration that creates it would then fail. A host whose migrations create the table must therefore apply them before the first request reaches the store.

2. **Keep the automatic upgrade and write the migration idempotently**, so it succeeds whether or not the store added the column first (either order is safe). Replace the generated `AddColumn` with:

   ```csharp
   // PostgreSQL: ADD COLUMN IF NOT EXISTS
   migrationBuilder.Sql("ALTER TABLE api_tokens ADD COLUMN IF NOT EXISTS \"Audience\" character varying(16) NULL;");

   // MariaDB: ADD COLUMN IF NOT EXISTS, with backtick-quoted identifiers
   migrationBuilder.Sql("ALTER TABLE `api_tokens` ADD COLUMN IF NOT EXISTS `Audience` varchar(16) NULL;");

   // SQL Server: guarded by COL_LENGTH
   migrationBuilder.Sql("IF COL_LENGTH('api_tokens', 'Audience') IS NULL ALTER TABLE [api_tokens] ADD [Audience] nvarchar(16) NULL;");
   ```

   MySQL (unlike MariaDB) has no `ADD COLUMN IF NOT EXISTS`: check `information_schema.COLUMNS` first, or use option 1. SQLite has no conditional `ADD COLUMN`; use option 1.

Existing rows keep `NULL`, which reads as `cli`.

## License

MIT
