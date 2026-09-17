using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sellora.Api.Contracts;
using Sellora.Domain.Permissions;

namespace Sellora.Api.Controllers;

[ApiController]
[Route("api/v1/permissions")]
[Authorize(Policy = "Permissions.View")]
public class PermissionsController : ControllerBase
{
    [HttpGet]
    public ActionResult<IReadOnlyList<PermissionDto>> List()
    {
        return Ok(PermissionCatalog.All.Select(PermissionDto.From).ToList());
    }
}
