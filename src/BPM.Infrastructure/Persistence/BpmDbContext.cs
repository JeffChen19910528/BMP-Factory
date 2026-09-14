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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(BpmDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }
}
