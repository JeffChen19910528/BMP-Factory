using BPM.Application.Common;
using BPM.Application.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BPM.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/users")]
public class UsersController : ControllerBase
{
    private readonly IUserService _userService;
    private readonly ICurrentUserService _currentUser;

    public UsersController(IUserService userService, ICurrentUserService currentUser)
    {
        _userService = userService;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<UserDto>>> GetAll(CancellationToken cancellationToken) =>
        Ok(await _userService.GetAllAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<UserDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var user = await _userService.GetByIdAsync(id, cancellationToken);
        return user is null ? NotFound() : Ok(user);
    }

    [HttpPost]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<UserDto>> Create(CreateUserRequest request, CancellationToken cancellationToken)
    {
        var user = await _userService.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = user.Id }, user);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<UserDto>> Update(Guid id, UpdateUserRequest request, CancellationToken cancellationToken)
    {
        var user = await _userService.UpdateAsync(id, request, cancellationToken);
        return user is null ? NotFound() : Ok(user);
    }

    // Phase 9 — Administrator-initiated password reset. No email/token workflow; this is a
    // direct administrative action for a user who can no longer authenticate any other way.
    [HttpPost("{id:guid}/reset-password")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<UserDto>> ResetPassword(Guid id, ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        var user = await _userService.ResetPasswordAsync(id, request, cancellationToken);
        return user is null ? NotFound() : Ok(user);
    }

    // Phase 9 — self-service. The route segment is literally "me", not a {id:guid} — there is no
    // way to name another user's id here at all, and the caller's identity always comes from the
    // authenticated JWT via ICurrentUserService, never the request body.
    [HttpPost("me/change-password")]
    public async Task<IActionResult> ChangeMyPassword(ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        await _userService.ChangePasswordAsync(_currentUser.RequireUserId(), request, cancellationToken);
        return NoContent();
    }
}
