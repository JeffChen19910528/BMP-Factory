using BPM.Application.Workflow;
using BPM.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BPM.Infrastructure.Services;

public class ProcessInstanceQueryService : IProcessInstanceQueryService
{
    private readonly BpmDbContext _db;

    public ProcessInstanceQueryService(BpmDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<ProcessInstanceDto>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await _db.ProcessInstances
            .AsNoTracking()
            .OrderByDescending(p => p.StartedAt)
            .Select(p => new ProcessInstanceDto(p.Id, p.ProcessDefinitionId, p.ProcessVersionId, p.BusinessKey, p.InitiatorId, p.Status, p.StartedAt, p.CompletedAt))
            .ToListAsync(cancellationToken);

    public async Task<ProcessInstanceDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        await _db.ProcessInstances
            .AsNoTracking()
            .Where(p => p.Id == id)
            .Select(p => new ProcessInstanceDto(p.Id, p.ProcessDefinitionId, p.ProcessVersionId, p.BusinessKey, p.InitiatorId, p.Status, p.StartedAt, p.CompletedAt))
            .SingleOrDefaultAsync(cancellationToken);
}
