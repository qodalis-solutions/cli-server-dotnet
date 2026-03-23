using Microsoft.AspNetCore.Mvc;
using Qodalis.Cli.Plugin.Admin.Services;

namespace Qodalis.Cli.Plugin.Admin.Controllers;

/// <summary>
/// Admin controller for listing and toggling registered CLI plugin modules.
/// </summary>
[ApiController]
[Route("api/v1/qcli/plugins")]
public class PluginsController : ControllerBase
{
    private readonly ModuleRegistry _moduleRegistry;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginsController"/> class.
    /// </summary>
    public PluginsController(ModuleRegistry moduleRegistry)
    {
        _moduleRegistry = moduleRegistry;
    }

    /// <summary>
    /// Lists all registered plugin modules with their status and command processors.
    /// </summary>
    [HttpGet]
    public IActionResult List()
    {
        return Ok(_moduleRegistry.List());
    }

    /// <summary>
    /// Gets details of a specific plugin by ID.
    /// </summary>
    /// <param name="id">The plugin identifier (module name).</param>
    [HttpGet("{id}")]
    public IActionResult GetById(string id)
    {
        var plugin = _moduleRegistry.GetById(id);
        if (plugin == null)
            return NotFound(new { error = "Plugin not found", code = "PLUGIN_NOT_FOUND" });

        return Ok(plugin);
    }

    /// <summary>
    /// Toggles the enabled/disabled state of a plugin module.
    /// </summary>
    /// <param name="id">The plugin identifier (module name).</param>
    [HttpPost("{id}/toggle")]
    public IActionResult Toggle(string id)
    {
        var result = _moduleRegistry.Toggle(id);
        if (result == null)
            return NotFound(new { error = "Plugin not found", code = "PLUGIN_NOT_FOUND" });

        var response = new Dictionary<string, object>
        {
            ["message"] = result.Enabled ? "Plugin enabled" : "Plugin disabled",
            ["enabled"] = result.Enabled,
        };

        if (result.Warning != null)
        {
            response["warning"] = result.Warning;
        }

        return Ok(response);
    }
}
