using System.Diagnostics;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseWebSockets();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Map("/ws", async context =>
{
    if (context.WebSockets.IsWebSocketRequest)
    {
        var webSocket = await context.WebSockets.AcceptWebSocketAsync();
        Console.WriteLine("WebSocket connected");

        // Start bash process
        var bashProcess = StartBashProcess();

        // Handle WebSocket communication
        await HandleWebSocketCommunication(webSocket, bashProcess);
    }
    else
    {
        context.Response.StatusCode = 400;
    }
});

app.Run();

Process StartBashProcess()
{
    var process = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName =  "bash",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        }
    };

    process.Start();

    if (Environment.OSVersion.Platform != PlatformID.Win32NT)
    {
        process.StandardInput.WriteLine("stty raw -echo");
    }

    process.StandardInput.WriteLine("stty raw -echo");

    return process;
}

async Task HandleWebSocketCommunication(System.Net.WebSockets.WebSocket webSocket, Process bashProcess)
{
    var buffer = new byte[1024 * 4];

    // Forward shell output to WebSocket
    var outputTask = Task.Run(async () =>
    {
        while (!bashProcess.HasExited)
        {
            var output = new char[1024];
            var count = await bashProcess.StandardOutput.ReadAsync(output, 0, output.Length);
            if (count > 0)
            {
                var data = new ArraySegment<byte>(Encoding.UTF8.GetBytes(output, 0, count));
                await webSocket.SendAsync(data, System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }
    });

    // Forward WebSocket input to shell
    while (webSocket.State == System.Net.WebSockets.WebSocketState.Open)
    {
        var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
        if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
        {
            await webSocket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
            break;
        }

        var input = Encoding.UTF8.GetString(buffer, 0, result.Count);
        await bashProcess.StandardInput.WriteAsync(input);
    }

    await outputTask;
    bashProcess.Kill();
}