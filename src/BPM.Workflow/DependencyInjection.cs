using BPM.Application.Forms;
using BPM.Application.Workflow;
using BPM.Workflow.Engine;
using Microsoft.Extensions.Configuration;
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

    // Phase 6.4 — mirrors BPM.EmailDelivery.DependencyInjection.AddNotificationDelivery's own
    // shape: BPM.Api's composition root only needs to call AddSlaScheduler(), not know about
    // SlaProcessor/SlaSchedulerWorker directly.
    public static IServiceCollection AddSlaScheduler(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SlaSchedulerSettings>(configuration.GetSection(SlaSchedulerSettings.SectionName));
        services.AddScoped<SlaProcessor>();
        services.AddHostedService<SlaSchedulerWorker>();
        return services;
    }
}
