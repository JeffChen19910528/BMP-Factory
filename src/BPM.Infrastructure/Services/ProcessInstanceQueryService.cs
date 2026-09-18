using BPM.Application.Common;
using BPM.Application.Workflow;
using BPM.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BPM.Infrastructure.Services;

public class ProcessInstanceQueryService : IProcessInstanceQueryService
{
    private const string AdministratorRole = "Administrator";

    private readonly BpmDbContext _db;

    public ProcessInstanceQueryService(BpmDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<ProcessInstanceDto>> GetAllAsync(Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, CancellationToken cancellationToken = default)
    {
        if (currentUserRoles.Contains(AdministratorRole))
        {
            return await _db.ProcessInstances
                .AsNoTracking()
                .OrderByDescending(p => p.StartedAt)
                .Select(p => new ProcessInstanceDto(p.Id, p.ProcessDefinitionId, p.ProcessVersionId, p.BusinessKey, p.InitiatorId, p.Status, p.StartedAt, p.CompletedAt))
                .ToListAsync(cancellationToken);
        }

        var visibleIds = await VisibleProcessInstanceIdsAsync(_db, currentUserId, currentUserRoles, cancellationToken);
        return await _db.ProcessInstances
            .AsNoTracking()
            .Where(p => visibleIds.Contains(p.Id))
            .OrderByDescending(p => p.StartedAt)
            .Select(p => new ProcessInstanceDto(p.Id, p.ProcessDefinitionId, p.ProcessVersionId, p.BusinessKey, p.InitiatorId, p.Status, p.StartedAt, p.CompletedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<ProcessInstanceDto?> GetByIdAsync(Guid id, Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, CancellationToken cancellationToken = default)
    {
        var instance = await _db.ProcessInstances
            .AsNoTracking()
            .Where(p => p.Id == id)
            .Select(p => new ProcessInstanceDto(p.Id, p.ProcessDefinitionId, p.ProcessVersionId, p.BusinessKey, p.InitiatorId, p.Status, p.StartedAt, p.CompletedAt))
            .SingleOrDefaultAsync(cancellationToken);
        if (instance is null)
        {
            return null;
        }

        if (currentUserRoles.Contains(AdministratorRole) || instance.InitiatorId == currentUserId)
        {
            return instance;
        }

        var isTaskAssignee = await _db.TaskInstances
            .AnyAsync(t => t.ProcessInstanceId == id
                && (t.AssigneeId == currentUserId || (t.AssigneeRole != null && currentUserRoles.Contains(t.AssigneeRole))), cancellationToken);
        var isApprovalParticipant = !isTaskAssignee && await _db.ApprovalAssignments
            .AnyAsync(a => a.ApprovalInstance!.TaskInstance!.ProcessInstanceId == id
                && (a.UserId == currentUserId || a.DelegatedToUserId == currentUserId), cancellationToken);
        if (isTaskAssignee || isApprovalParticipant)
        {
            return instance;
        }

        throw new ForbiddenAppException("PROCESS_INSTANCE_NOT_AUTHORIZED", "You are not authorized to view this process instance.");
    }

    // Phase 7.1 — internal (not private) so DashboardQueryService can reuse this exact authorized
    // id set for Process Overview aggregate counts, instead of duplicating a second, potentially
    // divergent "which process instances can this user see" rule (Part 30's explicit warning).
    internal static async Task<List<Guid>> VisibleProcessInstanceIdsAsync(BpmDbContext db, Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, CancellationToken cancellationToken)
    {
        var byInitiator = db.ProcessInstances.Where(p => p.InitiatorId == currentUserId).Select(p => p.Id);
        var byTask = db.TaskInstances
            .Where(t => t.AssigneeId == currentUserId || (t.AssigneeRole != null && currentUserRoles.Contains(t.AssigneeRole)))
            .Select(t => t.ProcessInstanceId);
        var byApproval = db.ApprovalAssignments
            .Where(a => a.UserId == currentUserId || a.DelegatedToUserId == currentUserId)
            .Select(a => a.ApprovalInstance!.TaskInstance!.ProcessInstanceId);

        return await byInitiator.Union(byTask).Union(byApproval).ToListAsync(cancellationToken);
    }
}
