using BPM.Domain.Common;
using BPM.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace BPM.Infrastructure.Persistence;

public class BpmDbContext : DbContext
{
    public BpmDbContext(DbContextOptions<BpmDbContext> options) : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public DbSet<ProcessDefinition> ProcessDefinitions => Set<ProcessDefinition>();
    public DbSet<ProcessVersion> ProcessVersions => Set<ProcessVersion>();
    public DbSet<ProcessInstance> ProcessInstances => Set<ProcessInstance>();
    public DbSet<TaskInstance> TaskInstances => Set<TaskInstance>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(BpmDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }

    // PostgreSQL has no server-generated rowversion type (unlike SQL Server), so the
    // AuditableEntity.RowVersion concurrency token is application-managed: stamp a fresh value on
    // every insert/update here, and each entity's configuration marks it as a plain concurrency
    // token (IsConcurrencyToken + ValueGeneratedNever) rather than IsRowVersion (Skill.md §33).
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampRowVersions();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampRowVersions();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void StampRowVersions()
    {
        foreach (var entry in ChangeTracker.Entries<AuditableEntity>())
        {
            if (entry.State is EntityState.Added or EntityState.Modified)
            {
                entry.Entity.RowVersion = Guid.NewGuid().ToByteArray();
            }
        }
    }
}
