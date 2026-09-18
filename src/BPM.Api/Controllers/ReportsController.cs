using BPM.Application.Common;
using BPM.Application.ProcessMonitoring;
using BPM.Application.Reports;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BPM.Api.Controllers;

// Phase 7.3 — Reporting, read-only. [Authorize] (any authenticated user, no role restriction) —
// IReportQueryService itself decides scope from the caller's JWT roles claim, exactly like
// DashboardController/ProcessMonitoringController already do (Part 26: nothing here justifies an
// Administrator-only restriction that Dashboard/Process Monitoring don't also have). Query
// parameters are strongly typed (ReportQuery) and bound by the model binder — there is no route/
// parameter that accepts a recipient/subject id to impersonate.
[ApiController]
[Authorize]
[Route("api/reports")]
public class ReportsController : ControllerBase
{
    private readonly IReportQueryService _reportQueryService;
    private readonly ICurrentUserService _currentUser;

    public ReportsController(IReportQueryService reportQueryService, ICurrentUserService currentUser)
    {
        _reportQueryService = reportQueryService;
        _currentUser = currentUser;
    }

    [HttpGet("summary")]
    public async Task<ActionResult<ReportSummaryResponse>> GetSummary([FromQuery] ReportQuery query, CancellationToken cancellationToken) =>
        Ok(await _reportQueryService.GetSummaryAsync(_currentUser.RequireUserId(), _currentUser.Roles, query, cancellationToken));

    [HttpGet("details")]
    public async Task<ActionResult<PagedResult<ProcessMonitoringItemDto>>> GetDetails([FromQuery] ReportQuery query, CancellationToken cancellationToken) =>
        Ok(await _reportQueryService.GetDetailsAsync(_currentUser.RequireUserId(), _currentUser.Roles, query, cancellationToken));

    // Reuses File(bytes, contentType, fileName) — the same existing response shape
    // AttachmentsController.Download already established for the one other file-download
    // endpoint in this codebase (Part 20/24) — no new download convention invented.
    [HttpGet("export")]
    public async Task<IActionResult> Export([FromQuery] ReportQuery query, CancellationToken cancellationToken)
    {
        var csv = await _reportQueryService.ExportCsvAsync(_currentUser.RequireUserId(), _currentUser.Roles, query, cancellationToken);
        var fileName = $"report-{DateTime.UtcNow:yyyyMMddHHmmss}.csv";
        return File(csv, "text/csv", fileName);
    }
}
