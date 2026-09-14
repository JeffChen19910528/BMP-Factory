using BPM.Application.Forms;
using BPM.Domain.Entities;
using BPM.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BPM.Infrastructure.Services;

public class FormAuthorizationService : IFormAuthorizationService
{
    private const string AdministratorRole = "Administrator";

    private readonly BpmDbContext _db;

    public FormAuthorizationService(BpmDbContext db)
    {
        _db = db;
    }

    public async Task<bool> CanEditAsync(FormInstance instance, Guid userId, IReadOnlyCollection<string> userRoles, CancellationToken cancellationToken = default)
    {
        if (userRoles.Contains(AdministratorRole) || instance.CreatedByUserId == userId)
        {
            return true;
        }

        if (instance.TaskInstanceId is not Guid taskInstanceId)
        {
            return false;
        }

        var task = await _db.TaskInstances.AsNoTracking().SingleOrDefaultAsync(t => t.Id == taskInstanceId, cancellationToken);
        if (task is null)
        {
            return false;
        }

        return task.AssigneeId == userId || (task.AssigneeRole is not null && userRoles.Contains(task.AssigneeRole));
    }

    public async Task<bool> CanViewAsync(FormInstance instance, Guid userId, IReadOnlyCollection<string> userRoles, CancellationToken cancellationToken = default)
    {
        if (await CanEditAsync(instance, userId, userRoles, cancellationToken))
        {
            return true;
        }

        if (instance.ProcessInstanceId is not Guid processInstanceId)
        {
            return false;
        }

        var initiatorId = await _db.ProcessInstances.AsNoTracking()
            .Where(p => p.Id == processInstanceId)
            .Select(p => (Guid?)p.InitiatorId)
            .SingleOrDefaultAsync(cancellationToken);
        if (initiatorId == userId)
        {
            return true;
        }

        return await _db.ApprovalAssignments.AsNoTracking()
            .AnyAsync(a => a.ApprovalInstance!.TaskInstance!.ProcessInstanceId == processInstanceId
                && (a.UserId == userId || a.DelegatedToUserId == userId), cancellationToken);
    }
}
