using BPM.Application.Forms;
using BPM.Application.Workflow;
using BPM.Workflow.Engine;
using Microsoft.Extensions.DependencyInjection;

namespace BPM.Workflow;

// Keeps BPM.Api's composition root from needing to reference the concrete
// BPM.Workflow.Engine.WorkflowEngine/FormEngine types directly.
public static class DependencyInjection
{
    public static IServiceCollection AddWorkflowEngine(this IServiceCollection services)
    {
        services.AddScoped<IWorkflowEngine, WorkflowEngine>();
        services.AddScoped<IFormEngine, FormEngine>();
        return services;
    }
}
