namespace BPM.Workflow.Validation;

public record WorkflowValidationError(string Code, string Message);

public class WorkflowValidationResult
{
    private readonly List<WorkflowValidationError> _errors = new();

    public IReadOnlyList<WorkflowValidationError> Errors => _errors;
    public bool IsValid => _errors.Count == 0;

    public void AddError(string code, string message) => _errors.Add(new WorkflowValidationError(code, message));
}
