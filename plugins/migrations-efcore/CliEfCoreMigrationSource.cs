using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Qodalis.Cli.Plugin.Migrations.EfCore;

/// <summary>
/// A <typeparamref name="TContext"/>'s EF Core migrations as a migration source: status from
/// <c>GetMigrations()</c> / <c>GetAppliedMigrationsAsync()</c>, applied and rolled back one migration at a time with
/// <see cref="IMigrator"/>, dry runs from <c>GenerateScript</c>. EF keeps its history in <c>__EFMigrationsHistory</c>
/// (so <c>dotnet ef</c> keeps working); the plugin records the runs.
/// </summary>
/// <typeparam name="TContext">The DbContext, from <see cref="IDbContextFactory{TContext}"/> or registered with <c>AddDbContext</c>.</typeparam>
public sealed class CliEfCoreMigrationSource<TContext>(string name) : ICliTrackedMigrationSource where TContext : DbContext
{
    public string Name { get; } = name;

    public async Task<IReadOnlyList<CliMigration>> LoadAsync(CliSqlSession session)
    {
        await using var lease = await CliEfCoreContexts.CreateAsync<TContext>(session);
        var ids = lease.Context.Database.GetMigrations().ToList();
        return ids.Select((id, i) =>
        {
            var previous = i > 0 ? ids[i - 1] : Migration.InitialDatabase;
            return new CliMigration
            {
                Id = id,
                Name = NameOf(id),
                Transaction = false,
                Up = CliMigrationStep.FromCode(s => MigrateAsync(s, id), s => ScriptAsync(s, previous, id)),
                Down = CliMigrationStep.FromCode(s => MigrateAsync(s, previous), s => ScriptAsync(s, id, previous)),
            };
        }).ToList();
    }

    public async Task<IReadOnlyList<string>> GetAppliedAsync(CliSqlSession session)
    {
        await using var lease = await CliEfCoreContexts.CreateAsync<TContext>(session);
        return (await lease.Context.Database.GetAppliedMigrationsAsync(session.CancellationToken)).ToList();
    }

    /// <summary><c>20261120111649_PublicApi</c> → <c>PublicApi</c>.</summary>
    private static string NameOf(string id)
    {
        var underscore = id.IndexOf('_');
        return underscore > 0 && underscore < id.Length - 1 ? id[(underscore + 1)..] : id;
    }

    private static async Task MigrateAsync(CliSqlSession session, string target)
    {
        await using var lease = await CliEfCoreContexts.CreateAsync<TContext>(session);
        await lease.Context.GetService<IMigrator>().MigrateAsync(target, session.CancellationToken);
    }

    private static async Task<string?> ScriptAsync(CliSqlSession session, string from, string to)
    {
        await using var lease = await CliEfCoreContexts.CreateAsync<TContext>(session);
        return lease.Context.GetService<IMigrator>().GenerateScript(from, to, MigrationsSqlGenerationOptions.Default);
    }
}

/// <summary>Creates a store's or the host's DbContext working on a run's connection.</summary>
public static class CliEfCoreContexts
{
    /// <summary>A context and what owns it; dispose it when done.</summary>
    public sealed class Lease<TContext>(TContext context, IAsyncDisposable? scope) : IAsyncDisposable where TContext : DbContext
    {
        public TContext Context { get; } = context;

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            if (scope != null) await scope.DisposeAsync();
        }
    }

    /// <summary>
    /// A <typeparamref name="TContext"/> from <see cref="IDbContextFactory{TContext}"/> (or a new scope's), on the
    /// session's connection and transaction.
    /// </summary>
    public static async Task<Lease<TContext>> CreateAsync<TContext>(CliSqlSession session) where TContext : DbContext
    {
        var (context, scope) = await CreateAsync<TContext>(session.Services, session.CancellationToken);
        context.Database.SetDbConnection(session.Connection);
        if (session.Transaction != null) await context.Database.UseTransactionAsync(session.Transaction, session.CancellationToken);
        return new Lease<TContext>(context, scope);
    }

    /// <summary>A new connection to <typeparamref name="TContext"/>'s database (closed), for the migration database's runs.</summary>
    public static DbConnection NewConnection<TContext>(IServiceProvider services) where TContext : DbContext
    {
        var (context, scope) = CreateAsync<TContext>(services, CancellationToken.None).GetAwaiter().GetResult();
        try
        {
            var current = context.Database.GetDbConnection();
            var connection = (DbConnection)Activator.CreateInstance(current.GetType())!;
            connection.ConnectionString = context.Database.GetConnectionString() ?? current.ConnectionString;
            return connection;
        }
        finally
        {
            context.Dispose();
            scope?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static async Task<(TContext, AsyncServiceScope?)> CreateAsync<TContext>(IServiceProvider services, CancellationToken ct)
        where TContext : DbContext
    {
        var factory = services.GetService<IDbContextFactory<TContext>>();
        if (factory != null) return (await factory.CreateDbContextAsync(ct), null);
        var scope = services.CreateAsyncScope();
        return (scope.ServiceProvider.GetRequiredService<TContext>(), scope);
    }
}
