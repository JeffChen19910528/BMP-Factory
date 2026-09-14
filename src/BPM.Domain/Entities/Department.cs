using BPM.Domain.Common;

namespace BPM.Domain.Entities;

public class Department : AuditableEntity
{
    public string Name { get; set; } = string.Empty;
    public Guid OrganizationId { get; set; }
    public Organization? Organization { get; set; }
    public Guid? ParentId { get; set; }
    public Department? Parent { get; set; }
    public ICollection<Department> Children { get; set; } = new List<Department>();

    // Used by AssignmentType.DepartmentManager / Manager resolution (Skill.md §10).
    public Guid? ManagerUserId { get; set; }

    public ICollection<User> Users { get; set; } = new List<User>();
}
