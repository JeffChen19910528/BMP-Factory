using BPM.Application.Common;
using BPM.Application.Roles;
using BPM.Application.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BPM.Api.Controllers;

[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/roles")]
public class RolesController : ControllerBase
{
    private readonly IRoleService _roleService;
    private readonly ICurrentUserService _currentUser;

    public RolesController(IRoleService roleService, ICurrentUserService currentUser)
    {
        _roleService = roleService;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<RoleDto>>> GetAll(CancellationToken cancellationToken) =>
        Ok(await _roleService.GetAllAsync(cancellationToken));

    [HttpGet("{id:guid}/members")]
    public async Task<ActionResult<IReadOnlyList<UserDto>>> GetMembers(Guid id, CancellationToken cancellationToken) =>
        Ok(await _roleService.GetMembersAsync(id, cancellationToken));

    [HttpPost]
    public async Task<ActionResult<RoleDto>> Create(CreateRoleRequest request, CancellationToken cancellationToken) =>
        Ok(await _roleService.CreateAsync(request, cancellationToken));

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<RoleDto>> Update(Guid id, UpdateRoleRequest request, CancellationToken cancellationToken)
    {
        var role = await _roleService.UpdateAsync(id, request, cancellationToken);
        return role is null ? NotFound() : Ok(role);
    }

    [HttpPost("assign")]
    public async Task<IActionResult> Assign(AssignRoleRequest request, CancellationToken cancellationToken)
    {
        await _roleService.AssignAsync(request, cancellationToken);
        return NoContent();
    }

    [HttpPost("unassign")]
    public async Task<IActionResult> Unassign(UnassignRoleRequest request, CancellationToken cancellationToken)
    {
        await _roleService.UnassignAsync(request, _currentUser.RequireUserId(), cancellationToken);
        return NoContent();
    }
}
