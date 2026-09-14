using BPM.Application.Workflow;
using BPM.Workflow.Engine;
using Microsoft.Extensions.DependencyInjection;

namespace BPM.Workflow;

// Keeps BPM.Api's composition root from needing to reference the concrete
// BPM.Workflow.Engine.WorkflowEngine type directly.
public static class DependencyInjection
{
    public static IServiceCollection AddWorkflowEngine(this IServiceCollection services)
    {
        services.AddScoped<IWorkflowEngine, WorkflowEngine>();
        return services;
    }
}
