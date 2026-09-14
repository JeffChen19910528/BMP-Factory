using BPM.Domain.Entities;

namespace BPM.Application.Workflow;

public record ApprovalAssignmentDto(
    Guid Id,
    Guid UserId,
    Guid? DelegatedToUserId,
    int Order,
    ApprovalAssignmentStatus Status,
    DateTime AssignedAt,
    DateTime? CompletedAt);

public record ApprovalSummaryDto(
    ApprovalPolicy Policy,
    ApprovalInstanceStatus Status,
    int RequiredCount,
    int ApprovedCount,
    int RejectedCount,
    IReadOnlyList<ApprovalAssignmentDto> Assignments);

public record DelegateTaskRequest(Guid DelegateToUserId);

public record TransferTaskRequest(Guid NewUserId, string? Reason);

public record AddApproverRequest(Guid UserId);
