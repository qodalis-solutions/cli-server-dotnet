using Microsoft.AspNetCore.Mvc;
using Qodalis.Cli.Plugin.Admin.Services;

namespace Qodalis.Cli.Plugin.Admin.Controllers;

[ApiController]
[Route("api/v1/qcli/plugins")]
public class PluginsController : ControllerBase
{
    private readonly ModuleRegistry _moduleRegistry;

    public PluginsController(ModuleRegistry moduleRegistry)
    {
        _moduleRegistry = moduleRegistry;
    }

    [HttpGet]
    public IActionResult List()
    {
        return Ok(_moduleRegistry.List());
    }

    [HttpGet("{id}")]
    public IActionResult GetById(string id)
    {
        var plugin = _moduleRegistry.GetById(id);
        if (plugin == null)
            return NotFound(new { error = "Plugin not found", code = "PLUGIN_NOT_FOUND" });

        return Ok(plugin);
    }

    [HttpPost("{id}/toggle")]
    public IActionResult Toggle(string id)
    {
        if (!_moduleRegistry.Toggle(id))
            return NotFound(new { error = "Plugin not found", code = "PLUGIN_NOT_FOUND" });

        var plugin = _moduleRegistry.GetById(id);
        return Ok(new
        {
            message = plugin!.Enabled ? "Plugin enabled" : "Plugin disabled",
            enabled = plugin.Enabled,
        });
    }
}
