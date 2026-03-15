using Microsoft.AspNetCore.Mvc;
using Qodalis.Cli.Services;

namespace Qodalis.Cli.Plugin.Admin.Controllers;

[ApiController]
[Route("api/v1/qcli/ws/clients")]
public class WsClientsController : ControllerBase
{
    private readonly CliEventSocketManager _eventSocketManager;

    public WsClientsController(CliEventSocketManager eventSocketManager)
    {
        _eventSocketManager = eventSocketManager;
    }

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
