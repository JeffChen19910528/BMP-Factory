using System.Text.Json;
using BPM.Application.Common;
using BPM.Application.Forms;
using BPM.Domain.Entities;
using BPM.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BPM.Infrastructure.Services;

public class FormInstanceQueryService : IFormInstanceQueryService
{
    private readonly BpmDbContext _db;
    private readonly IFormAuthorizationService _authorization;

    public FormInstanceQueryService(BpmDbContext db, IFormAuthorizationService authorization)
    {
        _db = db;
        _authorization = authorization;
    }

    public async Task<FormInstanceDto?> GetByIdAsync(Guid id, Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, CancellationToken cancellationToken = default)
    {
        var instance = await _db.FormInstances.AsNoTracking().SingleOrDefaultAsync(f => f.Id == id, cancellationToken);
        if (instance is null)
        {
            return null;
        }

        if (!await _authorization.CanViewAsync(instance, currentUserId, currentUserRoles, cancellationToken))
        {
            throw new ForbiddenAppException("FORM_INSTANCE_NOT_AUTHORIZED", "You are not authorized to view this form.");
        }

        return ToDto(instance);
    }

    public async Task<FormDataDto?> GetDataAsync(Guid formInstanceId, Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, CancellationToken cancellationToken = default)
    {
        var instance = await _db.FormInstances.AsNoTracking().SingleOrDefaultAsync(f => f.Id == formInstanceId, cancellationToken);
        if (instance is null)
        {
            return null;
        }

        if (!await _authorization.CanViewAsync(instance, currentUserId, currentUserRoles, cancellationToken))
        {
            throw new ForbiddenAppException("FORM_INSTANCE_NOT_AUTHORIZED", "You are not authorized to view this form.");
        }

        var data = await _db.FormData.AsNoTracking().SingleOrDefaultAsync(d => d.FormInstanceId == formInstanceId, cancellationToken);
        return data is null ? null : new FormDataDto(formInstanceId, JsonSerializer.Deserialize<JsonElement>(data.DataJson), Convert.ToBase64String(data.RowVersion));
    }

    public async Task<IReadOnlyList<FormInstanceDto>> GetByProcessInstanceAsync(Guid processInstanceId, Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, CancellationToken cancellationToken = default)
    {
        var instances = await _db.FormInstances.AsNoTracking()
            .Where(f => f.ProcessInstanceId == processInstanceId)
            .ToListAsync(cancellationToken);

        var visible = new List<FormInstanceDto>();
        foreach (var instance in instances)
        {
            if (await _authorization.CanViewAsync(instance, currentUserId, currentUserRoles, cancellationToken))
            {
                visible.Add(ToDto(instance));
            }
        }

        return visible;
    }

    private static FormInstanceDto ToDto(FormInstance instance) =>
        new(instance.Id, instance.FormDefinitionId, instance.FormVersionId, instance.ProcessInstanceId, instance.TaskInstanceId, instance.CreatedByUserId, instance.Status);
}
