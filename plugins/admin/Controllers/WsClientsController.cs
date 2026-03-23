using Microsoft.AspNetCore.Mvc;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Plugin.Admin.Controllers;

/// <summary>
/// Admin controller for listing active WebSocket client connections.
/// </summary>
[ApiController]
[Route("api/v1/qcli/ws/clients")]
public class WsClientsController : ControllerBase
{
    private readonly CliEventSocketManager _eventSocketManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="WsClientsController"/> class.
    /// </summary>
    public WsClientsController(CliEventSocketManager eventSocketManager)
    {
        _eventSocketManager = eventSocketManager;
    }

    /// <summary>
    /// Returns a list of all active WebSocket clients with connection metadata.
    /// </summary>
    [HttpGet]
    public IActionResult GetClients()
    {
        var clients = _eventSocketManager.GetClients();
        return Ok(new
        {
            clients = clients.Select(c => new
            {
                id = c.Id,
                connectedAt = c.ConnectedAt,
                remoteAddress = c.RemoteAddress,
                type = c.Type,
            }),
            total = clients.Count,
        });
    }
}
