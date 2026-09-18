using BPM.Application.Common;
using BPM.Application.ProcessMonitoring;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BPM.Api.Controllers;

// Phase 7.2.1 — Process Monitoring, read-only. [Authorize] (any authenticated user, no role
// restriction) — IProcessMonitoringQueryService itself decides scope from the caller's JWT roles
// claim, exactly like DashboardController/IProcessInstanceQueryService.GetAllAsync already do.
// Query parameters are strongly typed (ProcessMonitoringQuery) and bound by the model binder —
// there is no route/parameter that accepts a recipient/subject id to impersonate; InitiatorId can
// only narrow an already-authorized result set (see ProcessMonitoringQueryService's own comment).
[ApiController]
[Authorize]
[Route("api/process-monitoring")]
public class ProcessMonitoringController : ControllerBase
{
    private readonly IProcessMonitoringQueryService _queryService;
    private readonly IProcessInstanceDetailQueryService _detailQueryService;
    private readonly ICurrentUserService _currentUser;

    public ProcessMonitoringController(IProcessMonitoringQueryService queryService, IProcessInstanceDetailQueryService detailQueryService, ICurrentUserService currentUser)
    {
        _queryService = queryService;
        _detailQueryService = detailQueryService;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResult<ProcessMonitoringItemDto>>> Get([FromQuery] ProcessMonitoringQuery query, CancellationToken cancellationToken) =>
        Ok(await _queryService.GetAsync(_currentUser.RequireUserId(), _currentUser.Roles, query, cancellationToken));

    // Phase 7.2.2 — read-only Process Instance Detail + Timeline. 404 (not found) vs 403
    // (unauthorized) both come straight from IProcessInstanceDetailQueryService, which delegates
    // its entire authorization decision to the existing IProcessInstanceQueryService.GetByIdAsync
    // — the same outcome Task Detail's own GetById above already produces, not a second rule.
    [HttpGet("{processInstanceId:guid}")]
    public async Task<ActionResult<ProcessInstanceDetailDto>> GetDetail(Guid processInstanceId, CancellationToken cancellationToken)
    {
        var detail = await _detailQueryService.GetDetailAsync(processInstanceId, _currentUser.RequireUserId(), _currentUser.Roles, cancellationToken);
        return detail is null ? NotFound() : Ok(detail);
    }
}
