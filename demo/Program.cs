using Qodalis.Cli.Abstractions.Config;
using Qodalis.Cli.Extensions;
using Qodalis.Cli.Services;
using Qodalis.Cli.Demo;
using Qodalis.Cli.Processors;
using Qodalis.Cli.Plugin.Weather;
using Qodalis.Cli.Plugin.FileSystem;
using Qodalis.Cli.Plugin.Jobs;
using Qodalis.Cli.Demo.Jobs;
using Qodalis.Cli.Plugin.Admin.Extensions;
using Qodalis.Cli.Plugin.DataExplorer.Sql;
using Qodalis.Cli.Abstractions.DataExplorer;
using Qodalis.Cli.Plugin.DataExplorer.Mongo;
using Qodalis.Cli.Plugin.DataExplorer.Postgres;
using Qodalis.Cli.Plugin.DataExplorer.Mysql;
using Qodalis.Cli.Plugin.DataExplorer.Mssql;
using Qodalis.Cli.Plugin.DataExplorer.Redis;
using Qodalis.Cli.Plugin.DataExplorer.Elasticsearch;
using Qodalis.Cli.Plugin.Aws;
using Qodalis.Cli.Plugin.Users.Extensions;
using Qodalis.Cli.Plugin.PublicApi;
using Qodalis.Cli.Plugin.Docs;
using Qodalis.Cli.Plugin.Portal;
using Qodalis.Cli.Plugin.Config.EfCore;
using Qodalis.Cli.Plugin.Config.Redis;
using Microsoft.EntityFrameworkCore;
// Uncomment the using directive for the storage provider you want to use:
// using Qodalis.Cli.Plugin.FileSystem.Json;
// using Qodalis.Cli.Plugin.FileSystem.Sqlite;
// using Qodalis.Cli.Plugin.FileSystem.EfCore;
// using Qodalis.Cli.Plugin.FileSystem.S3;
// using Qodalis.Cli.Plugin.Jobs.EfCore;

var builder = WebApplication.CreateBuilder(args);

// CORS and WebSocket origins are restricted to the local dev front-ends
// (configured below with cli.SetAllowedOrigins; extra origins via QCLI_ALLOWED_ORIGINS, comma-separated).
var allowedOrigins = new List<string>
{
    "http://localhost:4200", "http://localhost:4300", "http://localhost:4301",
    "http://localhost:4302", "http://localhost:4303",
    "http://127.0.0.1:4200", "http://127.0.0.1:4300", "http://127.0.0.1:4301",
    "http://127.0.0.1:4302", "http://127.0.0.1:4303",
};
allowedOrigins.AddRange((Environment.GetEnvironmentVariable("QCLI_ALLOWED_ORIGINS") ?? string.Empty)
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

builder.Services
    .AddControllers()
    .AddCli(cli =>
    {
        cli.SetAllowedOrigins(allowedOrigins.ToArray());
        // The demo exposes the interactive shell explicitly (library default: disabled).
        // The users plugin is installed (below), so the shell runs in "users" mode: a WebSocket
        // ticket of a session holding shell:execute (root/admins). Shells close when the session
        // ends or loses the permission; limits, idle timeout and a minimal environment apply.
        cli.EnableShell(o =>
        {
            o.IdleTimeout = TimeSpan.FromMinutes(15);
            o.MaxSessionsPerUser = 3;
        });

        cli.AddModule(new DemoModule());
        cli.AddProcessor<CliHttpCommandProcessor>();
        cli.AddModule(new WeatherModule());
        cli.AddModule(new AwsModule());

        cli.AddJob<SampleHealthCheckJob>(o =>
        {
            o.Name = "health-check";
            o.Description = "Periodic health check";
            o.Group = "monitoring";
            o.Tags = ["health", "demo"];
            o.Interval = TimeSpan.FromSeconds(30);
        });

        // ---------------------------------------------------------------
        // Job Storage Provider Configuration
        // ---------------------------------------------------------------
        // By default, job execution history is stored in memory and lost
        // on restart. Use a persistent provider for durable storage.
        //
        // EF Core job storage (any EF-supported database):
        //
        // cli.AddEfCoreJobStorage(o => o.UseSqlite("Data Source=./data/jobs.db"));
        // ---------------------------------------------------------------

        // ---------------------------------------------------------------
        // File Storage Provider Configuration
        // ---------------------------------------------------------------
        // The file system API (/api/cli/fs/*) supports pluggable storage
        // providers. Choose ONE of the options below.
        //
        // Option 1: In-memory storage (default if no provider is set)
        //   Files are stored in memory and lost on restart.
        //   Good for testing and ephemeral workloads.
        //
        // cli.AddFileSystem(o => o.UseInMemory());
        //
        // Option 2: OS file system with allowed path restrictions
        //   Reads/writes to the host file system. AllowedPaths restricts
        //   which directories can be accessed (prevents path traversal).
        //
        // The demo uses a sandboxed in-memory file system — it never exposes the host's real files.
        cli.AddFileSystem(o => o.UseInMemory());
        //
        // To expose host directories instead (only behind authentication, never the real /home or /app):
        //
        // cli.AddFileSystem(o => o.UseOsFileSystem(os =>
        // {
        //     os.AllowedPaths = new List<string> { "/srv/qcli-sandbox" };
        // }));
        //
        // Option 3: JSON file storage
        //   Persists all files into a single JSON file on disk.
        //   Lightweight persistence without external dependencies.
        //
        // cli.AddFileSystem(o => o.UseJsonFile("./data/files.json"));
        //
        // Option 4: SQLite storage
        //   Persists files into a SQLite database.
        //   Good balance of simplicity and durability.
        //
        // cli.AddFileSystem(o => o.UseSqlite("./data/files.db"));
        //
        // Option 5: EF Core storage
        //   Uses Entity Framework Core with any supported database.
        //   Requires configuring DbContextOptions for your database provider.
        //
        // var efOptions = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<FileStorageDbContext>()
        //     .UseSqlite("Data Source=./data/files-ef.db")
        //     .Options;
        // cli.AddFileSystem(o => o.UseEfCore(efOptions));
        //
        // Option 6: Amazon S3 storage
        //   Persists files into an S3 bucket. Suitable for production
        //   cloud deployments.
        //
        // cli.AddFileSystem(o => o.UseS3(s3 =>
        // {
        //     s3.Bucket = "my-cli-files";
        //     s3.Region = "us-east-1";
        //     s3.Prefix = "uploads/";
        //     s3.AccessKeyId = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
        //     s3.SecretAccessKey = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");
        // }));
        // ---------------------------------------------------------------

        cli.AddDataExplorerSql("Data Source=demo.db", options =>
        {
            options.Name = "demo-sqlite";
            options.Description = "Demo SQLite database";
            options.Language = DataExplorerLanguage.Sql;
            options.DefaultOutputFormat = DataExplorerOutputFormat.Table;
            options.Templates =
            [
                new DataExplorerTemplate
                {
                    Name = "list_tables",
                    Query = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;",
                    Description = "List all tables"
                },
            ];
        });

        // -----------------------------------------------------------
        // Data Explorer — MongoDB Provider
        // -----------------------------------------------------------
        var mongoConnectionString = Environment.GetEnvironmentVariable("MONGO_CONNECTION_STRING");
        if (!string.IsNullOrEmpty(mongoConnectionString))
        {
            cli.AddDataExplorerMongo(mongoConnectionString, "demo", options =>
            {
                options.Name = "demo-mongo";
                options.Description = "Demo MongoDB database";
                options.Language = DataExplorerLanguage.Json;
                options.DefaultOutputFormat = DataExplorerOutputFormat.Json;
                options.Templates =
                [
                    new DataExplorerTemplate
                    {
                        Name = "show_collections",
                        Query = "show collections",
                        Description = "List all collections"
                    },
                    new DataExplorerTemplate
                    {
                        Name = "find_all",
                        Query = "db.users.find({})",
                        Description = "Find all documents in users collection"
                    },
                ];
            });
        }

        // -----------------------------------------------------------
        // Data Explorer — PostgreSQL Provider
        // -----------------------------------------------------------
        var pgConnectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
        if (!string.IsNullOrEmpty(pgConnectionString))
        {
            cli.AddDataExplorerPostgres(pgConnectionString, options =>
            {
                options.Name = "demo-postgres";
                options.Description = "Demo PostgreSQL database";
                options.Language = DataExplorerLanguage.Sql;
                options.DefaultOutputFormat = DataExplorerOutputFormat.Table;
            });
        }

        // -----------------------------------------------------------
        // Data Explorer — MySQL Provider
        // -----------------------------------------------------------
        var mysqlConnectionString = Environment.GetEnvironmentVariable("MYSQL_CONNECTION_STRING");
        if (!string.IsNullOrEmpty(mysqlConnectionString))
        {
            cli.AddDataExplorerMysql(mysqlConnectionString, options =>
            {
                options.Name = "demo-mysql";
                options.Description = "Demo MySQL database";
                options.Language = DataExplorerLanguage.Sql;
                options.DefaultOutputFormat = DataExplorerOutputFormat.Table;
            });
        }

        // -----------------------------------------------------------
        // Data Explorer — MS SQL Provider
        // -----------------------------------------------------------
        var mssqlConnectionString = Environment.GetEnvironmentVariable("MSSQL_CONNECTION_STRING");
        if (!string.IsNullOrEmpty(mssqlConnectionString))
        {
            cli.AddDataExplorerMssql(mssqlConnectionString, options =>
            {
                options.Name = "demo-mssql";
                options.Description = "Demo MS SQL Server database";
                options.Language = DataExplorerLanguage.Sql;
                options.DefaultOutputFormat = DataExplorerOutputFormat.Table;
            });
        }

        // -----------------------------------------------------------
        // Data Explorer — Redis Provider
        // -----------------------------------------------------------
        var redisConnectionString = Environment.GetEnvironmentVariable("REDIS_CONNECTION_STRING");
        if (!string.IsNullOrEmpty(redisConnectionString))
        {
            cli.AddDataExplorerRedis(redisConnectionString, options =>
            {
                options.Name = "demo-redis";
                options.Description = "Demo Redis instance";
                options.Language = DataExplorerLanguage.Redis;
                options.DefaultOutputFormat = DataExplorerOutputFormat.Table;
            });
        }

        // -----------------------------------------------------------
        // Data Explorer — Elasticsearch Provider
        // -----------------------------------------------------------
        var esNode = Environment.GetEnvironmentVariable("ELASTICSEARCH_NODE");
        if (!string.IsNullOrEmpty(esNode))
        {
            cli.AddDataExplorerElasticsearch(esNode, options =>
            {
                options.Name = "demo-elasticsearch";
                options.Description = "Demo Elasticsearch cluster";
                options.Language = DataExplorerLanguage.Elasticsearch;
                options.DefaultOutputFormat = DataExplorerOutputFormat.Json;
            });
        }

        // Demo credentials root/root (a warning is logged). Without DefaultAdminPassword a random
        // password is generated, logged once, and must be changed on first login.
        // MCP endpoint (POST /api/qcli/mcp): AI clients use the commands, files, data explorer and jobs
        // as tools with the caller's permissions (users plugin sessions or API tokens).
        cli.AddMcp(o => o.Instructions = "Tools of the Qodalis CLI demo server: commands, files, data explorer and jobs.");

        cli.AddUsers(o => o.DefaultAdminPassword = "root");
        cli.AddAdmin();

        // ---------------------------------------------------------------
        // Config stores: settings changed from the dashboard's Config page, `config set` or MCP `config_set` are kept
        // and applied again at the next start. Each provider may keep its settings in a store of its own:
        //
        //   - the default store, for every provider without one: a JSON file (QCLI_DEMO_CONFIG)
        //   - `demo` (the app's own settings below): the app's appsettings.json, section Qcli:Config, next to the rest
        //     of its configuration (IConfiguration sees the change: Qcli:Config:demo:greeting:values:text)
        //   - `jobs` (history retention): the demo's SQLite database (QCLI_DEMO_DB), table QcliConfig, through EF Core
        //   - `custom` (free settings): Redis when QCLI_DEMO_REDIS_URL is set (redis://localhost:6379/0)
        //
        // `config list` and GET /api/v1/qcli/config show the store of every provider; the host's store for an id
        // wins over the provider's own (cli.SetConfigStore(store, id), ConfigureConfig(o => o.Stores[id] = store)).
        // ---------------------------------------------------------------
        cli.SetConfigStore(new JsonFileConfigStore(Environment.GetEnvironmentVariable("QCLI_DEMO_CONFIG")
            ?? Path.Combine(Path.GetTempPath(), "qcli-demo-config.json")));
        cli.SetAppSettingsConfigStore("demo");
        cli.AddEfCoreConfigStore(o => o.UseSqlite($"Data Source={Environment.GetEnvironmentVariable("QCLI_DEMO_DB")
            ?? Path.Combine(Path.GetTempPath(), "qcli-demo.sqlite")}"), providerId: "jobs");
        if (Environment.GetEnvironmentVariable("QCLI_DEMO_REDIS_URL") is { Length: > 0 } redisUrl)
            cli.AddRedisConfigStore(o => o.Url = redisUrl, providerId: "custom");
        // A host app's own settings: what the demo's greeting says, and a secret it never shows
        cli.AddConfigProvider(new CliStoredConfigProvider(new CliStoredConfigProviderOptions
        {
            Id = "demo",
            Title = "Demo app",
            Description = "The demo host application's own settings",
            Sections =
            [
                new CliConfigSectionDefinition
                {
                    Id = "greeting",
                    Title = "Greeting",
                    Settings =
                    [
                        new CliConfigSettingDefinition
                        {
                            Key = "text", Label = "Greeting", Type = CliConfigEntryTypes.String,
                            Default = "Hello from the Qodalis CLI demo", Validation = new CliConfigValidation { Required = true, Max = 120 },
                        },
                        new CliConfigSettingDefinition
                        {
                            Key = "apiKey", Label = "Partner API key", Description = "Never shown, only whether it is set",
                            Type = CliConfigEntryTypes.Secret,
                        },
                    ],
                },
            ],
        }));

        // ---------------------------------------------------------------
        // Public API, guides and the portal site
        // ---------------------------------------------------------------
        // GET /v1/demo/time (below) is a public API route: only host tokens holding demo:read, created by the host
        // application (IApiTokenManager.CreateAsync(userId, new() { Name = "site", Audience = "host",
        // Permissions = ["demo:read"] })), 1000 requests a day and 60 a minute per account. Its OpenAPI document is
        // served at /qcli/api/openapi.json with Swagger UI at /qcli/api/swagger; the guides in ./docs at
        // /qcli/docs/pages; the portal site with both at /qcli/portal.
        cli.AddPermissions(p => p.Group("Demo").Add("demo:read", "The demo's public API"));
        cli.AddPublicApi(o => o.UseQuotaResolver((_, _, _) => ValueTask.FromResult(new PublicApiQuota(1000, 60))));
        cli.AddDocs(o =>
        {
            o.Name = "Qodalis CLI demo";
            o.Variables["support"] = "support@qodalis.com";
            o.AddFolder("docs");
        });
        cli.AddPortal(o => o.Name = "Qodalis CLI demo");
    })
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
    });

builder.Services.AddEndpointsApiExplorer();
// Schemas named by full type name: the users and admin plugins both have a LoginRequest.
builder.Services.AddSwaggerGen(o => o.CustomSchemaIds(type => type.FullName!.Replace('+', '.')));
// The public API's own document: only the routes in the "public" group.
builder.Services.AddOpenApi("public", o => o.ShouldInclude = d => d.GroupName == "public");

var app = builder.Build();

app.UseWebSockets();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.UseHttpsRedirection();
app.UseQodalisUsers();
app.UseQodalisAdmin();
app.UseQodalisDocs();
app.UseQodalisPublicApi();
app.UseQodalisPortal();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/v1/demo/time", () => new { utc = DateTimeOffset.UtcNow })
    .RequirePublicApi("demo:read")
    .WithGroupName("public")
    .WithTags("Demo")
    .WithSummary("The server's time (UTC)");
app.UseCli();

var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
var eventSocketManager = app.Services.GetRequiredService<ICliEventSocketManager>();
var logSocketManager = app.Services.GetRequiredService<ICliLogSocketManager>();
lifetime.ApplicationStopping.Register(() =>
{
    eventSocketManager.BroadcastDisconnectAsync().GetAwaiter().GetResult();
    logSocketManager.BroadcastDisconnectAsync().GetAwaiter().GetResult();
});

app.Run();
