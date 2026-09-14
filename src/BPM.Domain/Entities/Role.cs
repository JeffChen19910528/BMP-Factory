using BPM.Domain.Common;

namespace BPM.Domain.Entities;

public class Role : AuditableEntity
{
    public string Name { get; set; } = string.Empty;
    public ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();
}
