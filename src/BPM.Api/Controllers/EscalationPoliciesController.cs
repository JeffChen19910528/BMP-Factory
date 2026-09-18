using BPM.Application.Sla;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BPM.Api.Controllers;

// Phase 6.4 — minimal escalation policy configuration, mirroring SlaPoliciesController exactly:
// fully Administrator-only at the controller level, no non-admin consumer.
[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/escalation-policies")]
public class EscalationPoliciesController : ControllerBase
{
    private readonly IEscalationPolicyService _escalationPolicyService;

    public EscalationPoliciesController(IEscalationPolicyService escalationPolicyService)
    {
        _escalationPolicyService = escalationPolicyService;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<EscalationPolicyDto>>> GetAll(CancellationToken cancellationToken) =>
        Ok(await _escalationPolicyService.GetAllAsync(cancellationToken));

    [HttpPost]
    public async Task<ActionResult<EscalationPolicyDto>> Create(CreateEscalationPolicyRequest request, CancellationToken cancellationToken) =>
        Ok(await _escalationPolicyService.CreateAsync(request, cancellationToken));

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<EscalationPolicyDto>> Update(Guid id, UpdateEscalationPolicyRequest request, CancellationToken cancellationToken)
    {
        var policy = await _escalationPolicyService.UpdateAsync(id, request, cancellationToken);
        return policy is null ? NotFound() : Ok(policy);
    }
}
