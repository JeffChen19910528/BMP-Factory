using BPM.Application.Audit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BPM.Api.Controllers;

// Read-only by design — Skill.md §26 forbids modifying or deleting audit logs via the API.
[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/audit-logs")]
public class AuditLogsController : ControllerBase
{
    private readonly IAuditLogQueryService _auditLogQueryService;

    public AuditLogsController(IAuditLogQueryService auditLogQueryService)
    {
        _auditLogQueryService = auditLogQueryService;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AuditLogDto>>> Query([FromQuery] AuditLogQuery query, CancellationToken cancellationToken) =>
        Ok(await _auditLogQueryService.QueryAsync(query, cancellationToken));
}
