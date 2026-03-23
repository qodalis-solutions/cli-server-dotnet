using Qodalis.Cli.Extensions;
using Qodalis.Cli.Services;
using Qodalis.Cli.Demo.Processors;
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
// Uncomment the using directive for the storage provider you want to use:
// using Qodalis.Cli.Plugin.FileSystem.Json;
// using Qodalis.Cli.Plugin.FileSystem.Sqlite;
// using Qodalis.Cli.Plugin.FileSystem.EfCore;
// using Qodalis.Cli.Plugin.FileSystem.S3;
// using Qodalis.Cli.Plugin.Jobs.EfCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

builder.Services
    .AddControllers()
    .AddCli(cli =>
    {
        cli.AddProcessor<CliEchoCommandProcessor>();
        cli.AddProcessor<CliStatusCommandProcessor>();
        cli.AddProcessor<CliTimeCommandProcessor>();
        cli.AddProcessor<CliHelloCommandProcessor>();
        cli.AddProcessor<CliMathCommandProcessor>();
        cli.AddProcessor<CliSystemCommandProcessor>();
        cli.AddProcessor<CliHttpCommandProcessor>();
        cli.AddProcessor<CliHashCommandProcessor>();
        cli.AddProcessor<CliBase64CommandProcessor>();
        cli.AddModule(new WeatherModule());
        cli.AddModule(new AwsModule());

        cli.AddJob<SampleHealthCheckJob>(o =>
        {
            o.Name = "health-check";
            o.Description = "Periodic health check";
            o.Group = "monitoring";
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
        cli.AddFileSystem(o => o.UseOsFileSystem(os =>
        {
            os.AllowedPaths = new List<string> { "/tmp", "/app", "/home" };
        }));
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

        cli.AddAdmin();
    })
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseWebSockets();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.UseHttpsRedirection();
app.UseQodalisAdmin();
app.UseAuthorization();
app.MapControllers();
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
