using BPM.Application.Analytics;
using BPM.Application.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BPM.Api.Controllers;

// Phase 7.4 — Analytics, read-only. [Authorize] (any authenticated user, no role restriction) —
// same reasoning as ReportsController/ProcessMonitoringController/DashboardController:
// IAnalyticsQueryService itself decides scope from the caller's JWT roles claim.
[ApiController]
[Authorize]
[Route("api/analytics")]
public class AnalyticsController : ControllerBase
{
    private readonly IAnalyticsQueryService _analyticsQueryService;
    private readonly ICurrentUserService _currentUser;

    public AnalyticsController(IAnalyticsQueryService analyticsQueryService, ICurrentUserService currentUser)
    {
        _analyticsQueryService = analyticsQueryService;
        _currentUser = currentUser;
    }

    [HttpGet("overview")]
    public async Task<ActionResult<AnalyticsOverviewResponse>> GetOverview([FromQuery] AnalyticsQuery query, CancellationToken cancellationToken) =>
        Ok(await _analyticsQueryService.GetOverviewAsync(_currentUser.RequireUserId(), _currentUser.Roles, query, cancellationToken));
}
