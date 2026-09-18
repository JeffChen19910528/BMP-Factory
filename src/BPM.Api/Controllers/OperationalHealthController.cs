using BPM.Application.Administration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BPM.Api.Controllers;

// Phase 9 — Administrator-only diagnostic view (Part 32). Never exposes secrets — see
// OperationalHealthService's own comment on exactly which values it reads and why nothing else.
[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/operational-health")]
public class OperationalHealthController : ControllerBase
{
    private readonly IOperationalHealthService _operationalHealthService;

    public OperationalHealthController(IOperationalHealthService operationalHealthService)
    {
        _operationalHealthService = operationalHealthService;
    }

    [HttpGet]
    public async Task<ActionResult<OperationalHealthResponse>> Get(CancellationToken cancellationToken) =>
        Ok(await _operationalHealthService.GetAsync(cancellationToken));
}
