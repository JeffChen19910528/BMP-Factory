using BPM.Domain.Workflow;

namespace BPM.Application.Workflow;

// Mirrors BPM.Workflow.Validation.WorkflowValidationError/-Result, kept as a separate Application-
// layer DTO rather than exposing the Workflow project's validator types directly — Application
// (and BPM.Api, which references it) shouldn't need a reference to BPM.Workflow.Validation just
// to read a validation outcome shape. WorkflowEngine (the only IWorkflowEngine implementation)
// maps between the two.
public record WorkflowValidationErrorDto(string Code, string Message);

public record WorkflowValidationResultDto(bool IsValid, IReadOnlyList<WorkflowValidationErrorDto> Errors);

public record ValidateWorkflowDefinitionRequest(WorkflowDefinition Definition);
