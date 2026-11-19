using Microsoft.AspNetCore.Mvc;
using Qodalis.Cli.Plugin.Users.Services;
using Qodalis.Cli.Security;

namespace Qodalis.Cli.Plugin.Users.Controllers;

/// <summary>
/// The permission catalog (<see cref="ICliPermissionCatalog"/>) for the permission pickers:
/// <c>GET /permissions</c> — the entries the caller holds (a scoped token: its scope);
/// <c>?all=true</c> with <c>users:read</c> — every entry.
/// </summary>
[Route("api/v1/qcli/permissions")]
public class PermissionsController : UsersControllerBase
{
    private readonly ICliPermissionCatalog _catalog;

    public PermissionsController(ICliPermissionCatalog catalog)
    {
        _catalog = catalog;
    }

    [HttpGet]
    public IActionResult Get()
    {
        var caller = Auth;
        // Read raw: model binding turns ?all= into null, which must answer 400 like any other value.
        string? all = Request.Query.TryGetValue("all", out var values) ? values.ToString() : null;
        if (all != null && all != "true" && all != "false")
            throw UsersApiException.BadRequest("all must be true or false");
        var listAll = all == "true";
        if (listAll && !caller.Can(UsersPermissions.UsersRead))
            throw Denied(UsersPermissions.UsersRead);

        return JsonOk(listAll
            ? _catalog.All
            : _catalog.All.Where(p => PermissionMatcher.HasPermission(caller.Permissions, p.Name)).ToList());
    }
}
