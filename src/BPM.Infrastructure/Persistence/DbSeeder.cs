using BPM.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace BPM.Infrastructure.Persistence;

// Idempotent bootstrap seed for a fresh environment: an Administrator role and a first
// administrator account. Run explicitly at startup (see Program.cs) — never relied upon to
// exist implicitly elsewhere.
public static class DbSeeder
{
    public static async Task SeedAsync(BpmDbContext db, IPasswordHasher<User> passwordHasher, CancellationToken cancellationToken = default)
    {
        await db.Database.MigrateAsync(cancellationToken);

        var adminRole = await db.Roles.SingleOrDefaultAsync(r => r.Name == "Administrator", cancellationToken);
        if (adminRole is null)
        {
            adminRole = new Role { Name = "Administrator" };
            db.Roles.Add(adminRole);
            await db.SaveChangesAsync(cancellationToken);
        }

        var adminExists = await db.Users.AnyAsync(u => u.Username == "admin", cancellationToken);
        if (!adminExists)
        {
            var admin = new User
            {
                Username = "admin",
                DisplayName = "System Administrator",
                Email = "admin@bpm.local",
                IsActive = true,
            };
            admin.PasswordHash = passwordHasher.HashPassword(admin, "ChangeMe123!");
            db.Users.Add(admin);
            await db.SaveChangesAsync(cancellationToken);

            db.UserRoles.Add(new UserRole { UserId = admin.Id, RoleId = adminRole.Id });
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
