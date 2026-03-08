using Qodalis.Cli.Extensions;
using Qodalis.Cli.Services;
using Qodalis.Cli.Demo.Processors;
using Qodalis.Cli.Plugin.Weather;
using Qodalis.Cli.Plugin.FileSystem;
// Uncomment the using directive for the file storage provider you want to use:
// using Qodalis.Cli.Plugin.FileSystem.Json;
// using Qodalis.Cli.Plugin.FileSystem.Sqlite;
// using Qodalis.Cli.Plugin.FileSystem.EfCore;
// using Qodalis.Cli.Plugin.FileSystem.S3;

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
        cli.AddProcessor<CliUuidCommandProcessor>();
        cli.AddModule(new WeatherModule());

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
app.UseAuthorization();
app.MapControllers();
app.UseCli();

var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
var eventSocketManager = app.Services.GetRequiredService<CliEventSocketManager>();
lifetime.ApplicationStopping.Register(() =>
{
    eventSocketManager.BroadcastDisconnectAsync().GetAwaiter().GetResult();
});

app.Run();
