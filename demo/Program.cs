using Qodalis.Cli.Extensions;
using Qodalis.Cli.Services;
using Qodalis.Cli.Demo.Processors;

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
