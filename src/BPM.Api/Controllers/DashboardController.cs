using BPM.Application.Common;
using BPM.Application.Dashboard;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BPM.Api.Controllers;

// Phase 7.1 — one endpoint, scoped entirely by the authenticated caller (Part 3/10: no userId
// route segment or query parameter anywhere here — the same "no recipient/subject parameter"
// pattern NotificationsController already established). [Authorize] (any authenticated user, no
// role restriction) — DashboardQueryService itself decides how much a caller sees, based on
// whether their JWT roles claim includes Administrator, exactly like
// IProcessInstanceQueryService.GetAllAsync's own admin/non-admin branching.
[ApiController]
[Authorize]
[Route("api/dashboard")]
public class DashboardController : ControllerBase
{
    private readonly IDashboardQueryService _dashboardQueryService;
    private readonly ICurrentUserService _currentUser;

    public DashboardController(IDashboardQueryService dashboardQueryService, ICurrentUserService currentUser)
    {
        _dashboardQueryService = dashboardQueryService;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<ActionResult<DashboardResponse>> Get(CancellationToken cancellationToken) =>
        Ok(await _dashboardQueryService.GetDashboardAsync(_currentUser.RequireUserId(), _currentUser.Roles, cancellationToken));
}
