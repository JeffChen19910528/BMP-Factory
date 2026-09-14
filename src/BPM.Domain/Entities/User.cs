using BPM.Domain.Common;

namespace BPM.Domain.Entities;

public class User : AuditableEntity
{
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public Guid? DepartmentId { get; set; }
    public Department? Department { get; set; }
    public bool IsActive { get; set; } = true;

    // Manager for AssignmentType.Manager resolution (Skill.md §10).
    public Guid? ManagerUserId { get; set; }

    public string PasswordHash { get; set; } = string.Empty;

    public ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();
}
