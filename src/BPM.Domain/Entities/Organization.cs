using BPM.Domain.Common;

namespace BPM.Domain.Entities;

public class Organization : AuditableEntity
{
    public string Name { get; set; } = string.Empty;
    public Guid? ParentId { get; set; }
    public Organization? Parent { get; set; }
    public ICollection<Organization> Children { get; set; } = new List<Organization>();
    public ICollection<Department> Departments { get; set; } = new List<Department>();
}
