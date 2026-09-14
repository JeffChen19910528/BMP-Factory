using BPM.Application.Common;
using BPM.Application.Users;
using BPM.Domain.Entities;
using BPM.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace BPM.Infrastructure.Services;

public class UserService : IUserService
{
    private readonly BpmDbContext _db;
    private readonly IPasswordHasher<User> _passwordHasher;
    private readonly IAuditService _auditService;

    public UserService(BpmDbContext db, IPasswordHasher<User> passwordHasher, IAuditService auditService)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _auditService = auditService;
    }

    public async Task<IReadOnlyList<UserDto>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await _db.Users
            .AsNoTracking()
            .Select(u => new UserDto(u.Id, u.Username, u.DisplayName, u.Email, u.DepartmentId, u.IsActive))
            .ToListAsync(cancellationToken);

    public async Task<UserDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        await _db.Users
            .AsNoTracking()
            .Where(u => u.Id == id)
            .Select(u => new UserDto(u.Id, u.Username, u.DisplayName, u.Email, u.DepartmentId, u.IsActive))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<UserDto> CreateAsync(CreateUserRequest request, CancellationToken cancellationToken = default)
    {
        var user = new User
        {
            Username = request.Username,
            DisplayName = request.DisplayName,
            Email = request.Email,
            DepartmentId = request.DepartmentId,
        };
        user.PasswordHash = _passwordHasher.HashPassword(user, request.Password);

        _db.Users.Add(user);
        await _db.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync("CreateUser", nameof(User), user.Id.ToString(), newValue: new { user.Username, user.Email }, cancellationToken: cancellationToken);

        return new UserDto(user.Id, user.Username, user.DisplayName, user.Email, user.DepartmentId, user.IsActive);
    }

    public async Task<UserDto?> UpdateAsync(Guid id, UpdateUserRequest request, CancellationToken cancellationToken = default)
    {
        var user = await _db.Users.SingleOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (user is null)
        {
            return null;
        }

        var oldValue = new { user.DisplayName, user.Email, user.DepartmentId, user.IsActive };

        user.DisplayName = request.DisplayName;
        user.Email = request.Email;
        user.DepartmentId = request.DepartmentId;
        user.IsActive = request.IsActive;

        await _db.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync(
            "UpdateUser",
            nameof(User),
            user.Id.ToString(),
            oldValue,
            new { user.DisplayName, user.Email, user.DepartmentId, user.IsActive },
            cancellationToken);

        return new UserDto(user.Id, user.Username, user.DisplayName, user.Email, user.DepartmentId, user.IsActive);
    }
}
