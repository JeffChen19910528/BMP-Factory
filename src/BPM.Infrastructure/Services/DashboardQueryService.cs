using BPM.Application.Common;
using BPM.Application.Dashboard;
using BPM.Domain.Entities;
using BPM.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BPM.Infrastructure.Services;

// Phase 7.1 — Dashboard Foundation. Aggregates existing, already-authorized data — never a second
// authorization implementation, never a second copy of Task/Process/Approval/SLA business rules.
// Administrator gets a system-wide (unfiltered) view; everyone else gets a view scoped to exactly
// what TaskQueryService.BuildMyTaskIdsQuery / ProcessInstanceQueryService.VisibleProcessInstanceIdsAsync
// / the ApprovalAssignment participant predicate already consider "theirs" — the same branching
// shape ProcessInstanceQueryService.GetAllAsync itself already uses (unfiltered for Administrator,
// filtered by a reused id-set otherwise).
//
// Dashboard is a read-only operational snapshot (Part 22): the five sections below are populated
// by independent queries, not one shared transaction/snapshot isolation level — a task could be
// completed between the TaskSummary query and the SlaSummary query below, and the two numbers
// could momentarily disagree by one row. This is an accepted, documented tradeoff for an
// operational dashboard, not a correctness bug to "fix" with locks or a single mega-query.
public class DashboardQueryService : IDashboardQueryService
{
    private const string AdministratorRole = "Administrator";

    // Part 7: only these AuditLog actions are meaningful Dashboard activity — never scheduler
    // polling noise, never every audit action that exists (e.g. PermissionChange, FormDataUpdated
    // would be noise here, not signal).
    private static readonly string[] MeaningfulActions =
    {
        AuditActions.StartProcess,
        AuditActions.ProcessCompleted,
        AuditActions.Reject,
        AuditActions.TaskCompleted,
        AuditActions.ApprovalApproved,
        AuditActions.ApprovalRejected,
        AuditActions.ApprovalReturned,
    };

    private const int ActivityCandidateLimit = 100;
    private const int ActivityDisplayLimit = 10;

    private readonly BpmDbContext _db;
    private readonly IClock _clock;
    private readonly DashboardSettings _settings;

    public DashboardQueryService(BpmDbContext db, IClock clock, IOptions<DashboardSettings> settings)
    {
        _db = db;
        _clock = clock;
        _settings = settings.Value;
    }

    public async Task<DashboardResponse> GetDashboardAsync(Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, CancellationToken cancellationToken = default)
    {
        var isAdministrator = currentUserRoles.Contains(AdministratorRole);
        var now = _clock.UtcNow;

        var myTasks = await TaskSummaryAsync(currentUserId, currentUserRoles, isAdministrator, now, cancellationToken);
        var pendingApprovals = await ApprovalSummaryAsync(currentUserId, isAdministrator, cancellationToken);
        var sla = await SlaSummaryAsync(currentUserId, currentUserRoles, isAdministrator, cancellationToken);
        var processOverview = await ProcessSummaryAsync(currentUserId, currentUserRoles, isAdministrator, cancellationToken);
        var recentActivity = await RecentActivityAsync(currentUserId, currentUserRoles, isAdministrator, cancellationToken);

        return new DashboardResponse(myTasks, pendingApprovals, sla, processOverview, recentActivity);
    }

    // ---- My Tasks ----

    private async Task<DashboardTaskSummaryDto> TaskSummaryAsync(Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, bool isAdministrator, DateTime now, CancellationToken cancellationToken)
    {
        var dueSoonThreshold = now.AddHours(_settings.DueSoonHours);

        IQueryable<TaskInstance> scopedTasks;
        IQueryable<TaskSla> scopedSlas;
        if (isAdministrator)
        {
            scopedTasks = _db.TaskInstances;
            scopedSlas = _db.TaskSlas;
        }
        else
        {
            // Reuses TaskQueryService's own authorization computation — never a second "which
            // tasks can this user see" rule (Part 1/30).
            var myTaskIds = TaskQueryService.BuildMyTaskIdsQuery(_db, currentUserId, currentUserRoles);
            scopedTasks = _db.TaskInstances.Where(t => myTaskIds.Contains(t.Id));
            scopedSlas = _db.TaskSlas.Where(s => myTaskIds.Contains(s.TaskInstanceId));
        }

        var total = await scopedTasks.CountAsync(t => t.Status == TaskInstanceStatus.Pending || t.Status == TaskInstanceStatus.InProgress, cancellationToken);

        // Overdue/DueSoon come from TaskSla — Phase 6.4's own authoritative SLA state, never
        // recomputed from DueAt/DateTime.Now here (Part 5/13/29: the scheduler's own Overdue
        // transition is the single source of truth). DueSoon and Overdue are mutually exclusive by
        // construction (DueSoon requires DueAt > now, i.e. not yet due; Overdue is a persisted
        // status only the scheduler ever sets).
        var overdue = await scopedSlas.CountAsync(s => s.Status == TaskSlaStatus.Overdue, cancellationToken);
        var dueSoon = await scopedSlas.CountAsync(s => s.Status == TaskSlaStatus.Active && s.DueAt > now && s.DueAt <= dueSoonThreshold, cancellationToken);

        return new DashboardTaskSummaryDto(total, overdue, dueSoon);
    }

    // ---- Pending Approvals ----

    private async Task<DashboardApprovalSummaryDto> ApprovalSummaryAsync(Guid currentUserId, bool isAdministrator, CancellationToken cancellationToken)
    {
        var pending = isAdministrator
            ? await _db.ApprovalAssignments.CountAsync(a => a.Status == ApprovalAssignmentStatus.Pending, cancellationToken)
            : await _db.ApprovalAssignments.CountAsync(a => (a.UserId == currentUserId || a.DelegatedToUserId == currentUserId) && a.Status == ApprovalAssignmentStatus.Pending, cancellationToken);

        return new DashboardApprovalSummaryDto(pending);
    }

    // ---- SLA Overview ----

    private async Task<DashboardSlaSummaryDto> SlaSummaryAsync(Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, bool isAdministrator, CancellationToken cancellationToken)
    {
        IQueryable<TaskSla> scopedSlas;
        if (isAdministrator)
        {
            scopedSlas = _db.TaskSlas;
        }
        else
        {
            var myTaskIds = TaskQueryService.BuildMyTaskIdsQuery(_db, currentUserId, currentUserRoles);
            scopedSlas = _db.TaskSlas.Where(s => myTaskIds.Contains(s.TaskInstanceId));
        }

        // Warning is never a persisted TaskSlaStatus (Phase 6.4 deliberately has no such member) —
        // it is Active split by whether WarningNotifiedAt has already been set by
        // SlaSchedulerWorker, the same persisted idempotency field Phase 6.4 itself uses. This is
        // not a fresh "WarningAt <= now" computation, which could disagree with what the scheduler
        // has actually decided (Part 29: never calculate Overdue/Warning differently from Phase 6.4).
        var active = await scopedSlas.CountAsync(s => s.Status == TaskSlaStatus.Active && s.WarningNotifiedAt == null, cancellationToken);
        var warning = await scopedSlas.CountAsync(s => s.Status == TaskSlaStatus.Active && s.WarningNotifiedAt != null, cancellationToken);
        var overdue = await scopedSlas.CountAsync(s => s.Status == TaskSlaStatus.Overdue, cancellationToken);
        var completed = await scopedSlas.CountAsync(s => s.Status == TaskSlaStatus.Completed, cancellationToken);

        return new DashboardSlaSummaryDto(active, warning, overdue, completed);
    }

    // ---- Process Overview ----

    private async Task<DashboardProcessSummaryDto> ProcessSummaryAsync(Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, bool isAdministrator, CancellationToken cancellationToken)
    {
        IQueryable<ProcessInstance> scoped;
        if (isAdministrator)
        {
            scoped = _db.ProcessInstances;
        }
        else
        {
            // Reuses ProcessInstanceQueryService's own authorization computation verbatim — Part
            // 30's explicit warning against a second, potentially divergent visibility rule.
            var visibleIds = await ProcessInstanceQueryService.VisibleProcessInstanceIdsAsync(_db, currentUserId, currentUserRoles, cancellationToken);
            scoped = _db.ProcessInstances.Where(p => visibleIds.Contains(p.Id));
        }

        var counts = await scoped
            .GroupBy(p => p.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        int CountOf(ProcessInstanceStatus status) => counts.SingleOrDefault(c => c.Status == status)?.Count ?? 0;

        return new DashboardProcessSummaryDto(
            Running: CountOf(ProcessInstanceStatus.Running),
            Completed: CountOf(ProcessInstanceStatus.Completed),
            Rejected: CountOf(ProcessInstanceStatus.Rejected));
    }

    // ---- Recent Activity ----

    private async Task<IReadOnlyList<DashboardActivityItemDto>> RecentActivityAsync(Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, bool isAdministrator, CancellationToken cancellationToken)
    {
        // Bounded candidate set (Part 11/32: never load the whole AuditLogs table) — the most
        // recent 100 meaningful actions system-wide, further filtered/authorized below. 100 is a
        // generous margin over the 10 actually displayed, so a normal user with several recent
        // authorized events isn't starved by other users' unrelated activity in between.
        var candidates = await _db.AuditLogs
            .AsNoTracking()
            .Where(a => MeaningfulActions.Contains(a.Action))
            .OrderByDescending(a => a.Timestamp)
            .Take(ActivityCandidateLimit)
            .Select(a => new { a.Timestamp, a.Action, a.EntityType, a.EntityId })
            .ToListAsync(cancellationToken);

        // Resolve each candidate's ProcessInstanceId, batched by EntityType (never a per-row
        // query) — AuditLog's EntityType/EntityId is a loose (string, string) reference that varies
        // by action (ProcessInstance directly; TaskInstance or ApprovalAssignment one/two hops
        // away), inspected before writing this method (see PROGRESS.md's Phase 7.1 section).
        var taskInstanceIds = candidates.Where(c => c.EntityType == nameof(TaskInstance) && c.EntityId is not null)
            .Select(c => Guid.Parse(c.EntityId!)).Distinct().ToList();
        var approvalAssignmentIds = candidates.Where(c => c.EntityType == nameof(ApprovalAssignment) && c.EntityId is not null)
            .Select(c => Guid.Parse(c.EntityId!)).Distinct().ToList();

        var taskToProcess = await _db.TaskInstances
            .Where(t => taskInstanceIds.Contains(t.Id))
            .Select(t => new { t.Id, t.ProcessInstanceId })
            .ToDictionaryAsync(x => x.Id, x => x.ProcessInstanceId, cancellationToken);
        var approvalToProcess = await _db.ApprovalAssignments
            .Where(a => approvalAssignmentIds.Contains(a.Id))
            .Select(a => new { a.Id, ProcessInstanceId = a.ApprovalInstance!.TaskInstance!.ProcessInstanceId })
            .ToDictionaryAsync(x => x.Id, x => x.ProcessInstanceId, cancellationToken);

        Guid? ResolveProcessInstanceId(string entityType, string? entityId)
        {
            if (entityId is null || !Guid.TryParse(entityId, out var parsed))
            {
                return null;
            }
            return entityType switch
            {
                nameof(ProcessInstance) => parsed,
                nameof(TaskInstance) => taskToProcess.GetValueOrDefault(parsed),
                nameof(ApprovalAssignment) => approvalToProcess.GetValueOrDefault(parsed),
                _ => null,
            };
        }

        var resolvedAuditActivity = candidates
            .Select(c => (Timestamp: c.Timestamp, Action: c.Action, ProcessInstanceId: (Guid?)ResolveProcessInstanceId(c.EntityType, c.EntityId)))
            .ToList();

        // SLA-triggered activity (SlaWarning/SlaOverdue/SlaEscalated) was never written to
        // AuditLog (Phase 6.4's own deliberate decision — see CLAUDE.md's SLA Scheduler note: "no
        // AuditLog entries were added for... individual Warning/Overdue/Escalation notification
        // creation"). Sourced from Notification instead, scoped to the caller's own notifications
        // — a Notification is inherently private to its RecipientUserId (Phase 6.1), so this needs
        // no additional authorization check of its own, unlike the AuditLog-derived items above.
        // Known limitation, documented rather than worked around: an Administrator's system-wide
        // Recent Activity still only ever includes the Administrator's *own* SLA notifications, not
        // every user's — there is no "system-wide notification feed" endpoint to reuse, and
        // building one is out of this phase's scope.
        var slaNotifications = await _db.Notifications
            .AsNoTracking()
            .Where(n => n.RecipientUserId == currentUserId
                && (n.Type == NotificationType.SlaWarning || n.Type == NotificationType.SlaOverdue || n.Type == NotificationType.SlaEscalated))
            .OrderByDescending(n => n.CreatedAt)
            .Take(ActivityDisplayLimit)
            .Select(n => new { n.CreatedAt, n.Type, n.RelatedEntityId })
            .ToListAsync(cancellationToken);

        var slaTaskIds = slaNotifications.Where(n => n.RelatedEntityId is not null).Select(n => Guid.Parse(n.RelatedEntityId!)).Distinct().ToList();
        var slaTaskToProcess = await _db.TaskInstances
            .Where(t => slaTaskIds.Contains(t.Id))
            .Select(t => new { t.Id, t.ProcessInstanceId })
            .ToDictionaryAsync(x => x.Id, x => x.ProcessInstanceId, cancellationToken);

        var resolvedSlaActivity = slaNotifications
            .Select(n => (
                Timestamp: n.CreatedAt,
                Action: n.Type.ToString(),
                ProcessInstanceId: (Guid?)(n.RelatedEntityId is not null && Guid.TryParse(n.RelatedEntityId, out var taskId) ? slaTaskToProcess.GetValueOrDefault(taskId) : null)))
            .ToList();

        var combined = resolvedAuditActivity.Concat(resolvedSlaActivity);

        IEnumerable<(DateTime Timestamp, string Action, Guid? ProcessInstanceId)> authorized;
        if (isAdministrator)
        {
            authorized = combined;
        }
        else
        {
            var visibleProcessIds = await ProcessInstanceQueryService.VisibleProcessInstanceIdsAsync(_db, currentUserId, currentUserRoles, cancellationToken);
            var visibleSet = visibleProcessIds.ToHashSet();
            // The SLA-notification-derived items are already inherently the caller's own
            // (RecipientUserId filter above) — they pass through regardless of process visibility,
            // since a private notification about the caller's own task is authorized by definition
            // even in the rare case the underlying TaskInstance lookup above returned nothing.
            authorized = resolvedAuditActivity.Where(a => a.ProcessInstanceId is Guid pid && visibleSet.Contains(pid))
                .Concat(resolvedSlaActivity);
        }

        return authorized
            .OrderByDescending(a => a.Timestamp)
            .Take(ActivityDisplayLimit)
            .Select(a => new DashboardActivityItemDto(a.Timestamp, a.Action, DescriptionFor(a.Action), a.ProcessInstanceId))
            .ToList();
    }

    private static string DescriptionFor(string action) => action switch
    {
        AuditActions.StartProcess => "Process started",
        AuditActions.ProcessCompleted => "Process completed",
        AuditActions.Reject => "Process rejected",
        AuditActions.TaskCompleted => "Task completed",
        AuditActions.ApprovalApproved => "Approval approved",
        AuditActions.ApprovalRejected => "Approval rejected",
        AuditActions.ApprovalReturned => "Approval returned",
        nameof(NotificationType.SlaWarning) => "SLA warning",
        nameof(NotificationType.SlaOverdue) => "SLA overdue",
        nameof(NotificationType.SlaEscalated) => "SLA escalated",
        _ => action,
    };
}
