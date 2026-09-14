using BPM.Application.Common;
using BPM.Domain.Workflow;
using BPM.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BPM.Workflow.Engine;

// Resolves a WorkflowAssignment to the concrete set of user ids it authorizes, on the backend,
// never trusting anything the frontend claims about who the approver is (Skill.md Phase 3 §7).
// Shared by plain UserTask assignment (always exactly one resolved user) and ApprovalTask's
// assignment list (each entry may resolve to many).
public static class AssignmentResolver
{
    public static async Task<IReadOnlyList<Guid>> ResolveAsync(BpmDbContext db, WorkflowAssignment assignment, Guid processInitiatorId, CancellationToken cancellationToken = default)
    {
        switch (assignment.Type)
        {
            case WorkflowAssignmentType.User:
                return new[] { Guid.Parse(assignment.Value) };

            case WorkflowAssignmentType.ProcessInitiator:
                return new[] { processInitiatorId };

            case WorkflowAssignmentType.Role:
                return await db.UserRoles
                    .Where(ur => ur.Role!.Name == assignment.Value && ur.User!.IsActive)
                    .Select(ur => ur.UserId)
                    .Distinct()
                    .ToListAsync(cancellationToken);

            case WorkflowAssignmentType.Department:
            {
                var departmentId = Guid.Parse(assignment.Value);
                return await db.Users
                    .Where(u => u.DepartmentId == departmentId && u.IsActive)
                    .Select(u => u.Id)
                    .ToListAsync(cancellationToken);
            }

            case WorkflowAssignmentType.DepartmentManager:
            {
                var departmentId = Guid.Parse(assignment.Value);
                var managerUserId = await db.Departments
                    .Where(d => d.Id == departmentId)
                    .Select(d => d.ManagerUserId)
                    .SingleOrDefaultAsync(cancellationToken);

                if (managerUserId is null)
                {
                    throw new ConflictAppException("ASSIGNMENT_UNRESOLVABLE", $"Department '{departmentId}' has no manager configured.");
                }

                return new[] { managerUserId.Value };
            }

            default:
                // Blocked at publish time by WorkflowDefinitionValidator's UNSUPPORTED_ASSIGNMENT_TYPE
                // check; defensive fail-closed guard in case a version predates that check.
                throw new ConflictAppException("UNSUPPORTED_ASSIGNMENT_TYPE", $"Assignment type '{assignment.Type}' is not yet supported.");
        }
    }

    // Resolves every entry in an ApprovalTask's assignment list and returns the deduplicated
    // union, in first-seen order (used as ApprovalAssignment.Order — see its doc comment).
    public static async Task<IReadOnlyList<Guid>> ResolveManyAsync(BpmDbContext db, IReadOnlyList<WorkflowAssignment> assignments, Guid processInitiatorId, CancellationToken cancellationToken = default)
    {
        var seen = new HashSet<Guid>();
        var ordered = new List<Guid>();

        foreach (var assignment in assignments)
        {
            var resolved = await ResolveAsync(db, assignment, processInitiatorId, cancellationToken);
            foreach (var userId in resolved.Where(seen.Add))
            {
                ordered.Add(userId);
            }
        }

        if (ordered.Count == 0)
        {
            throw new ConflictAppException("ASSIGNMENT_UNRESOLVABLE", "No users could be resolved for this approval task's assignments.");
        }

        return ordered;
    }
}
