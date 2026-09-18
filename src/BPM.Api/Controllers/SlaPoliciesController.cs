using BPM.Application.Sla;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BPM.Api.Controllers;

// Phase 6.3 — minimal backend/API-level SLA policy configuration (Part R: no policy designer UI
// this phase). Fully Administrator-only at the controller level, matching RolesController/
// AuditLogsController's own precedent — SLA policy is administrative configuration with no
// legitimate non-admin consumer, unlike Users/Departments (whose GETs are also used by
// non-admin assignment pickers elsewhere in the app).
[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/sla-policies")]
public class SlaPoliciesController : ControllerBase
{
    private readonly ISlaPolicyService _slaPolicyService;

    public SlaPoliciesController(ISlaPolicyService slaPolicyService)
    {
        _slaPolicyService = slaPolicyService;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SlaPolicyDto>>> GetAll(CancellationToken cancellationToken) =>
        Ok(await _slaPolicyService.GetAllAsync(cancellationToken));

    [HttpPost]
    public async Task<ActionResult<SlaPolicyDto>> Create(CreateSlaPolicyRequest request, CancellationToken cancellationToken) =>
        Ok(await _slaPolicyService.CreateAsync(request, cancellationToken));

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<SlaPolicyDto>> Update(Guid id, UpdateSlaPolicyRequest request, CancellationToken cancellationToken)
    {
        var policy = await _slaPolicyService.UpdateAsync(id, request, cancellationToken);
        return policy is null ? NotFound() : Ok(policy);
    }
}
