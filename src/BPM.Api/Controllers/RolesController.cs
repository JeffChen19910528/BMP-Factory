using BPM.Application.Roles;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BPM.Api.Controllers;

[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/roles")]
public class RolesController : ControllerBase
{
    private readonly IRoleService _roleService;

    public RolesController(IRoleService roleService)
    {
        _roleService = roleService;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<RoleDto>>> GetAll(CancellationToken cancellationToken) =>
        Ok(await _roleService.GetAllAsync(cancellationToken));

    [HttpPost]
    public async Task<ActionResult<RoleDto>> Create(CreateRoleRequest request, CancellationToken cancellationToken) =>
        Ok(await _roleService.CreateAsync(request, cancellationToken));

    [HttpPost("assign")]
    public async Task<IActionResult> Assign(AssignRoleRequest request, CancellationToken cancellationToken)
    {
        await _roleService.AssignAsync(request, cancellationToken);
        return NoContent();
    }
}
