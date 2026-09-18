using BPM.Application.Common;
using BPM.Application.Users;
using BPM.Domain.Entities;
using BPM.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace BPM.Infrastructure.Services;

public class UserService : IUserService
{
    private readonly BpmDbContext _db;
    private readonly IPasswordHasher<User> _passwordHasher;
    private readonly IAuditService _auditService;
    private readonly IValidator<CreateUserRequest> _createValidator;
    private readonly IValidator<UpdateUserRequest> _updateValidator;
    private readonly IValidator<ResetPasswordRequest> _resetPasswordValidator;
    private readonly IValidator<ChangePasswordRequest> _changePasswordValidator;

    public UserService(
        BpmDbContext db,
        IPasswordHasher<User> passwordHasher,
        IAuditService auditService,
        IValidator<CreateUserRequest> createValidator,
        IValidator<UpdateUserRequest> updateValidator,
        IValidator<ResetPasswordRequest> resetPasswordValidator,
        IValidator<ChangePasswordRequest> changePasswordValidator)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _auditService = auditService;
        _createValidator = createValidator;
        _updateValidator = updateValidator;
        _resetPasswordValidator = resetPasswordValidator;
        _changePasswordValidator = changePasswordValidator;
    }

    public async Task<IReadOnlyList<UserDto>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await _db.Users
            .AsNoTracking()
            .Select(u => ToDto(u))
            .ToListAsync(cancellationToken);

    public async Task<UserDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        await _db.Users
            .AsNoTracking()
            .Where(u => u.Id == id)
            .Select(u => ToDto(u))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<UserDto> CreateAsync(CreateUserRequest request, CancellationToken cancellationToken = default)
    {
        await _createValidator.ValidateAndThrowAsync(request, cancellationToken);

        // Phase 5.5.2: previously unchecked — a duplicate username/email surfaced as an
        // unhandled 500 from the unique index violation rather than a clean 409, the same class
        // of gap Phase 5.2 already closed for ProcessDefinition.Key.
        var usernameTaken = await _db.Users.AnyAsync(u => u.Username == request.Username, cancellationToken);
        if (usernameTaken)
        {
            throw new ConflictAppException("USERNAME_TAKEN", $"A user with username '{request.Username}' already exists.");
        }
        var emailTaken = await _db.Users.AnyAsync(u => u.Email == request.Email, cancellationToken);
        if (emailTaken)
        {
            throw new ConflictAppException("EMAIL_TAKEN", $"A user with email '{request.Email}' already exists.");
        }

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

        await _auditService.LogAsync(AuditActions.CreateUser, nameof(User), user.Id.ToString(), newValue: new { user.Username, user.Email }, cancellationToken: cancellationToken);

        return ToDto(user);
    }

    public async Task<UserDto?> UpdateAsync(Guid id, UpdateUserRequest request, CancellationToken cancellationToken = default)
    {
        await _updateValidator.ValidateAndThrowAsync(request, cancellationToken);

        var user = await _db.Users.SingleOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (user is null)
        {
            return null;
        }

        var emailTakenByAnotherUser = await _db.Users.AnyAsync(u => u.Id != id && u.Email == request.Email, cancellationToken);
        if (emailTakenByAnotherUser)
        {
            throw new ConflictAppException("EMAIL_TAKEN", $"A user with email '{request.Email}' already exists.");
        }

        var oldValue = new { user.DisplayName, user.Email, user.DepartmentId, user.IsActive };

        user.DisplayName = request.DisplayName;
        user.Email = request.Email;
        user.DepartmentId = request.DepartmentId;
        user.IsActive = request.IsActive;

        // Phase 5.5.2: User already had a RowVersion column configured as a concurrency token
        // (Phase 1), but nothing ever checked it — two administrators editing the same user
        // concurrently silently last-write-won. Wired up using the exact same pattern Phase
        // 5.3.2/5.4.1 established for ProcessVersion/FormVersion.
        byte[] expectedVersion;
        try
        {
            expectedVersion = Convert.FromBase64String(request.ExpectedVersion);
        }
        catch (FormatException)
        {
            throw new BadRequestAppException("INVALID_EXPECTED_VERSION", "ExpectedVersion must be a base64-encoded RowVersion.");
        }
        _db.Entry(user).Property(u => u.RowVersion).OriginalValue = expectedVersion;

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictAppException("USER_CONCURRENCY_CONFLICT", "This user was modified by another administrator. Reload and retry.");
        }

        await _auditService.LogAsync(
            "UpdateUser",
            nameof(User),
            user.Id.ToString(),
            oldValue,
            new { user.DisplayName, user.Email, user.DepartmentId, user.IsActive },
            cancellationToken);

        return ToDto(user);
    }

    // Phase 9 — Administrator-only (enforced by the controller). Never accepts or persists a
    // plaintext password anywhere except immediately through IPasswordHasher.HashPassword, the
    // exact same call CreateAsync already makes — no second hashing algorithm. The new password
    // itself is never included in the audit entry (Part 21) or returned in the response (ToDto
    // never exposes PasswordHash to begin with).
    public async Task<UserDto?> ResetPasswordAsync(Guid id, ResetPasswordRequest request, CancellationToken cancellationToken = default)
    {
        await _resetPasswordValidator.ValidateAndThrowAsync(request, cancellationToken);

        var user = await _db.Users.SingleOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (user is null)
        {
            return null;
        }

        user.PasswordHash = _passwordHasher.HashPassword(user, request.NewPassword);

        byte[] expectedVersion;
        try
        {
            expectedVersion = Convert.FromBase64String(request.ExpectedVersion);
        }
        catch (FormatException)
        {
            throw new BadRequestAppException("INVALID_EXPECTED_VERSION", "ExpectedVersion must be a base64-encoded RowVersion.");
        }
        _db.Entry(user).Property(u => u.RowVersion).OriginalValue = expectedVersion;

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictAppException("USER_CONCURRENCY_CONFLICT", "This user was modified by another administrator. Reload and retry.");
        }

        // Part 21 — audit carries no password material whatsoever, not even a length or hash.
        await _auditService.LogAsync(AuditActions.ResetPassword, nameof(User), user.Id.ToString(), cancellationToken: cancellationToken);

        return ToDto(user);
    }

    // Phase 9 — self-service. userId is always the authenticated caller (controller-supplied,
    // never trusted from the request body — there is no user identifier on ChangePasswordRequest
    // at all, so there is no endpoint surface through which a caller could even attempt to name a
    // different target). CurrentPassword is verified with the same IPasswordHasher/
    // VerifyHashedPassword call AuthService.LoginAsync already uses before any change is applied.
    public async Task ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken cancellationToken = default)
    {
        await _changePasswordValidator.ValidateAndThrowAsync(request, cancellationToken);

        var user = await _db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken)
            ?? throw new NotFoundAppException("USER_NOT_FOUND", "User not found.");

        var verifyResult = _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.CurrentPassword);
        if (verifyResult == PasswordVerificationResult.Failed)
        {
            throw new BadRequestAppException("INVALID_CURRENT_PASSWORD", "The current password is incorrect.");
        }

        user.PasswordHash = _passwordHasher.HashPassword(user, request.NewPassword);

        // No ExpectedVersion here (Part 17/19 — self-service carries no concurrency parameter);
        // EF's own change tracker still captured this row's RowVersion when it was loaded above,
        // so a genuinely concurrent conflicting write still throws DbUpdateConcurrencyException
        // here exactly as it would with an explicit OriginalValue pin — just without a client-
        // supplied version to detect staleness proactively before submitting.
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictAppException("USER_CONCURRENCY_CONFLICT", "This account was modified elsewhere. Reload and retry.");
        }

        await _auditService.LogAsync(AuditActions.ChangePassword, nameof(User), user.Id.ToString(), cancellationToken: cancellationToken);
    }

    private static UserDto ToDto(User u) =>
        new(u.Id, u.Username, u.DisplayName, u.Email, u.DepartmentId, u.IsActive, u.LastLoginAt, Convert.ToBase64String(u.RowVersion));
}
