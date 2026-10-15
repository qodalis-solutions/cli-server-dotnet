using Qodalis.Cli.Extensions;
using Qodalis.Cli.Services;
using Qodalis.Cli.Server.Processors;
using Qodalis.Cli.Server.Jobs;
using Qodalis.Cli.Plugin.Weather;
using Qodalis.Cli.Plugin.Jobs;
using Qodalis.Cli.Plugin.Admin.Extensions;
// using Qodalis.Cli.Plugin.Jobs.EfCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddControllers()
    .AddCli(cli =>
    {
        // Only the local dev front-ends may call this server or open its sockets.
        cli.SetAllowedOrigins(
            "http://localhost:4200", "http://localhost:4300", "http://localhost:4301",
            "http://localhost:4302", "http://localhost:4303", "http://127.0.0.1:*");
        // This host has no users plugin, so the shell needs its own access mode (otherwise it is
        // locked). Token mode: clients send "Authorization: Bearer <token>" (browser clients use
        // the server's configured headers, forwarded as ?_header_Authorization=...).
        // Set QCLI_SHELL_TOKEN or "Shell:Token" in configuration. Without a token the shell is
        // anonymous in Development only (startup warning) and locked elsewhere.
        var shellToken = builder.Configuration["Shell:Token"] ?? Environment.GetEnvironmentVariable("QCLI_SHELL_TOKEN");
        cli.EnableShell(o =>
        {
            o.Token = string.IsNullOrWhiteSpace(shellToken) ? null : shellToken;
            o.AllowAnonymous = o.Token == null && builder.Environment.IsDevelopment();
        });

        cli.AddProcessor<CliEchoCommandProcessor>();
        cli.AddProcessor<CliStatusCommandProcessor>();
        cli.AddProcessor<CliSystemCommandProcessor>();
        cli.AddProcessor<CliHttpCommandProcessor>();
        cli.AddProcessor<CliHashCommandProcessor>();
        cli.AddProcessor<CliBase64CommandProcessor>();
        cli.AddModule(new WeatherModule());
        cli.AddFileSystem(o => o.AllowedPaths.Add("/tmp"));
        cli.AddJob(new SampleHealthCheckJob(), o =>
        {
            o.Name = "health-check";
            o.Description = "Periodic health check that verifies system status";
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
