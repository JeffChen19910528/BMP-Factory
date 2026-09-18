using BPM.Application.Administration;
using BPM.Application.Audit;
using BPM.Application.Common;
using BPM.Application.Dashboard;
using BPM.Application.Departments;
using BPM.Application.ProcessMonitoring;
using BPM.Application.Forms;
using BPM.Application.Notifications;
using BPM.Application.Organizations;
using BPM.Application.Processes;
using BPM.Application.Analytics;
using BPM.Application.Reports;
using BPM.Application.Roles;
using BPM.Application.Sla;
using BPM.Application.Users;
using BPM.Application.Workflow;
using BPM.Domain.Entities;
using BPM.Infrastructure.Persistence;
using BPM.Infrastructure.Services;
using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BPM.Infrastructure;

// The composition root (BPM.Api's Program.cs) shouldn't need to know every concrete service
// type this layer provides — it just calls AddInfrastructure(). Adding a new Infrastructure
// service only touches this file, not Program.cs.
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Connection string 'Default' is missing.");

        services.AddDbContext<BpmDbContext>(options => options.UseNpgsql(connectionString));
        services.AddHealthChecks().AddNpgSql(connectionString, name: "postgres");

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUserService, CurrentUserService>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<IAuditLogQueryService, AuditLogQueryService>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IOrganizationService, OrganizationService>();
        services.AddScoped<IDepartmentService, DepartmentService>();
        services.AddScoped<IRoleService, RoleService>();
        services.AddScoped<IProcessDefinitionService, ProcessDefinitionService>();
        services.AddScoped<IProcessInstanceQueryService, ProcessInstanceQueryService>();
        services.AddScoped<ITaskQueryService, TaskQueryService>();
        services.AddScoped<IFormDefinitionService, FormDefinitionService>();
        services.AddScoped<IFormInstanceQueryService, FormInstanceQueryService>();
        services.AddScoped<IFormAuthorizationService, FormAuthorizationService>();
        services.AddScoped<IAttachmentService, AttachmentService>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<ISlaPolicyService, SlaPolicyService>();
        services.AddScoped<IEscalationPolicyService, EscalationPolicyService>();
        services.Configure<DashboardSettings>(configuration.GetSection(DashboardSettings.SectionName));
        services.AddScoped<IDashboardQueryService, DashboardQueryService>();
        services.AddScoped<IProcessMonitoringQueryService, ProcessMonitoringQueryService>();
        services.AddScoped<IReportQueryService, ReportQueryService>();
        services.AddScoped<IAnalyticsQueryService, AnalyticsQueryService>();
        services.AddScoped<IOperationalHealthService, OperationalHealthService>();
        services.AddScoped<IProcessInstanceDetailQueryService, ProcessInstanceDetailQueryService>();
        services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();

        services.Configure<MinioSettings>(configuration.GetSection(MinioSettings.SectionName));
        services.AddSingleton<IAttachmentStorage, MinioAttachmentStorage>();

        // Registered here (not in BPM.Application, where the validators themselves live) because
        // this is the layer that consumes them (ProcessDefinitionService injects IValidator<T>).
        services.AddValidatorsFromAssemblyContaining<CreateProcessDefinitionRequestValidator>();

        return services;
    }
}
