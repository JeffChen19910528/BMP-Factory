using BPM.Domain.Entities;

namespace BPM.Application.Workflow;

public record ProcessInstanceDto(
    Guid Id,
    Guid ProcessDefinitionId,
    Guid ProcessVersionId,
    string? BusinessKey,
    Guid InitiatorId,
    ProcessInstanceStatus Status,
    DateTime StartedAt,
    DateTime? CompletedAt);

public record StartProcessRequest(string ProcessDefinitionKey, string? BusinessKey);

public record TaskDto(
    Guid Id,
    Guid ProcessInstanceId,
    string NodeId,
    string NodeName,
    Guid? AssigneeId,
    string? AssigneeRole,
    TaskInstanceStatus Status,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    DateTime? DueAt);
