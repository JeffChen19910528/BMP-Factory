using BPM.Application.Common;
using BPM.Application.Departments;
using BPM.Domain.Entities;
using BPM.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BPM.Infrastructure.Services;

public class DepartmentService : IDepartmentService
{
    private readonly BpmDbContext _db;
    private readonly IAuditService _auditService;

    public DepartmentService(BpmDbContext db, IAuditService auditService)
    {
        _db = db;
        _auditService = auditService;
    }

    public async Task<IReadOnlyList<DepartmentDto>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await _db.Departments
            .AsNoTracking()
            .Select(d => new DepartmentDto(d.Id, d.Name, d.OrganizationId, d.ParentId, d.ManagerUserId))
            .ToListAsync(cancellationToken);

    public async Task<DepartmentDto> CreateAsync(CreateDepartmentRequest request, CancellationToken cancellationToken = default)
    {
        var department = new Department
        {
            Name = request.Name,
            OrganizationId = request.OrganizationId,
            ParentId = request.ParentId,
            ManagerUserId = request.ManagerUserId,
        };
        _db.Departments.Add(department);
        await _db.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync("CreateDepartment", nameof(Department), department.Id.ToString(), newValue: new { department.Name }, cancellationToken: cancellationToken);

        return new DepartmentDto(department.Id, department.Name, department.OrganizationId, department.ParentId, department.ManagerUserId);
    }
}
