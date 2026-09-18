using BPM.Application.Common;
using BPM.Application.Users;
using BPM.Domain.Entities;
using BPM.Infrastructure.Persistence;
using BPM.Infrastructure.Services;
using BPM.Tests.Workflow;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BPM.Tests.Administration;

// Phase 5.5.2 — Administration Foundation. UserService already existed (Phase 1); this phase
// added optimistic concurrency (User already had a RowVersion column configured since Phase 1,
// never surfaced) and duplicate username/email rejection — both genuine gaps found during this
// phase's own mandatory inspection, not a rewrite of the User domain.
[Collection("Postgres")]
public class UserServiceTests
{
    private static UserService NewService(BpmDbContext db) =>
        new(db, new PasswordHasher<User>(), new AuditService(db, new FixedCurrentUser(Guid.Empty)), new CreateUserRequestValidator(), new UpdateUserRequestValidator(), new ResetPasswordRequestValidator(), new ChangePasswordRequestValidator());

    [Fact]
    public async Task CreateAsync_ValidRequest_Succeeds_AndReturnsRowVersion()
    {
        await using var db = PostgresFixture.CreateContext();
        var request = new CreateUserRequest($"user-{Guid.NewGuid():N}", "Test User", $"{Guid.NewGuid():N}@bpm-tests.local", "Passw0rd!123", null);

        var user = await NewService(db).CreateAsync(request);

        Assert.Equal(request.Username, user.Username);
        Assert.True(user.IsActive);
        Assert.NotEmpty(user.RowVersion);
    }

    [Fact]
    public async Task CreateAsync_DuplicateUsername_Rejected()
    {
        var username = $"user-{Guid.NewGuid():N}";
        await using var db1 = PostgresFixture.CreateContext();
        await NewService(db1).CreateAsync(new CreateUserRequest(username, "First", $"{Guid.NewGuid():N}@bpm-tests.local", "Passw0rd!123", null));

        await using var db2 = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<ConflictAppException>(() =>
            NewService(db2).CreateAsync(new CreateUserRequest(username, "Second", $"{Guid.NewGuid():N}@bpm-tests.local", "Passw0rd!123", null)));
        Assert.Equal("USERNAME_TAKEN", ex.Code);
    }

    [Fact]
    public async Task CreateAsync_DuplicateEmail_Rejected()
    {
        var email = $"{Guid.NewGuid():N}@bpm-tests.local";
        await using var db1 = PostgresFixture.CreateContext();
        await NewService(db1).CreateAsync(new CreateUserRequest($"user-{Guid.NewGuid():N}", "First", email, "Passw0rd!123", null));

        await using var db2 = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<ConflictAppException>(() =>
            NewService(db2).CreateAsync(new CreateUserRequest($"user-{Guid.NewGuid():N}", "Second", email, "Passw0rd!123", null)));
        Assert.Equal("EMAIL_TAKEN", ex.Code);
    }

    [Fact]
    public async Task CreateAsync_InvalidEmail_RejectedByValidation()
    {
        await using var db = PostgresFixture.CreateContext();
        await Assert.ThrowsAsync<FluentValidation.ValidationException>(() =>
            NewService(db).CreateAsync(new CreateUserRequest($"user-{Guid.NewGuid():N}", "Test", "not-an-email", "Passw0rd!123", null)));
    }

    [Fact]
    public async Task UpdateAsync_ValidRequest_Succeeds_AndAdvancesRowVersion()
    {
        await using var createDb = PostgresFixture.CreateContext();
        var created = await NewService(createDb).CreateAsync(new CreateUserRequest($"user-{Guid.NewGuid():N}", "Original Name", $"{Guid.NewGuid():N}@bpm-tests.local", "Passw0rd!123", null));

        await using var updateDb = PostgresFixture.CreateContext();
        var updated = await NewService(updateDb).UpdateAsync(created.Id, new UpdateUserRequest("Updated Name", created.Email, null, false, created.RowVersion));

        Assert.NotNull(updated);
        Assert.Equal("Updated Name", updated!.DisplayName);
        Assert.False(updated.IsActive);
        Assert.NotEqual(created.RowVersion, updated.RowVersion);
    }

    [Fact]
    public async Task UpdateAsync_StaleExpectedVersion_RejectedWithConflict_LoserWriteNotApplied()
    {
        await using var createDb = PostgresFixture.CreateContext();
        var created = await NewService(createDb).CreateAsync(new CreateUserRequest($"user-{Guid.NewGuid():N}", "Original Name", $"{Guid.NewGuid():N}@bpm-tests.local", "Passw0rd!123", null));

        // Writer A saves successfully using the version both writers loaded.
        await using var writerADb = PostgresFixture.CreateContext();
        await NewService(writerADb).UpdateAsync(created.Id, new UpdateUserRequest("Writer A's Name", created.Email, null, true, created.RowVersion));

        // Writer B still has the original (now-stale) version.
        await using var writerBDb = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<ConflictAppException>(() =>
            NewService(writerBDb).UpdateAsync(created.Id, new UpdateUserRequest("Writer B's Name", created.Email, null, true, created.RowVersion)));
        Assert.Equal("USER_CONCURRENCY_CONFLICT", ex.Code);

        await using var verifyDb = PostgresFixture.CreateContext();
        var reloaded = await NewService(verifyDb).GetByIdAsync(created.Id);
        Assert.Equal("Writer A's Name", reloaded!.DisplayName);
    }

    [Fact]
    public async Task UpdateAsync_MalformedExpectedVersion_RejectedAsBadRequest()
    {
        await using var createDb = PostgresFixture.CreateContext();
        var created = await NewService(createDb).CreateAsync(new CreateUserRequest($"user-{Guid.NewGuid():N}", "Name", $"{Guid.NewGuid():N}@bpm-tests.local", "Passw0rd!123", null));

        await using var db = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<BadRequestAppException>(() =>
            NewService(db).UpdateAsync(created.Id, new UpdateUserRequest("Name", created.Email, null, true, "not-base64!!")));
        Assert.Equal("INVALID_EXPECTED_VERSION", ex.Code);
    }

    [Fact]
    public async Task UpdateAsync_NonexistentUser_ReturnsNull()
    {
        await using var db = PostgresFixture.CreateContext();
        var result = await NewService(db).UpdateAsync(Guid.NewGuid(), new UpdateUserRequest("Name", "x@bpm-tests.local", null, true, Convert.ToBase64String(new byte[8])));
        Assert.Null(result);
    }

    [Fact]
    public async Task UpdateAsync_EmailTakenByAnotherUser_Rejected()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var service = NewService(setupDb);
        var userA = await service.CreateAsync(new CreateUserRequest($"user-{Guid.NewGuid():N}", "A", $"{Guid.NewGuid():N}@bpm-tests.local", "Passw0rd!123", null));
        var userB = await service.CreateAsync(new CreateUserRequest($"user-{Guid.NewGuid():N}", "B", $"{Guid.NewGuid():N}@bpm-tests.local", "Passw0rd!123", null));

        await using var db = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<ConflictAppException>(() =>
            NewService(db).UpdateAsync(userB.Id, new UpdateUserRequest("B", userA.Email, null, true, userB.RowVersion)));
        Assert.Equal("EMAIL_TAKEN", ex.Code);
    }

    // ---- Phase 9: Administrator password reset ----

    [Fact]
    public async Task ResetPasswordAsync_ValidRequest_AllowsLoginWithNewPassword_NotOldPassword()
    {
        await using var createDb = PostgresFixture.CreateContext();
        var hasher = new PasswordHasher<User>();
        var created = await NewService(createDb).CreateAsync(new CreateUserRequest($"user-{Guid.NewGuid():N}", "Name", $"{Guid.NewGuid():N}@bpm-tests.local", "OldPassw0rd!", null));

        await using var resetDb = PostgresFixture.CreateContext();
        var result = await NewService(resetDb).ResetPasswordAsync(created.Id, new ResetPasswordRequest("NewPassw0rd!", created.RowVersion));
        Assert.NotNull(result);

        await using var verifyDb = PostgresFixture.CreateContext();
        var storedUser = await verifyDb.Users.SingleAsync(u => u.Id == created.Id);
        Assert.Equal(PasswordVerificationResult.Success, hasher.VerifyHashedPassword(storedUser, storedUser.PasswordHash, "NewPassw0rd!"));
        Assert.Equal(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(storedUser, storedUser.PasswordHash, "OldPassw0rd!"));
    }

    [Fact]
    public async Task ResetPasswordAsync_NonexistentUser_ReturnsNull()
    {
        await using var db = PostgresFixture.CreateContext();
        var result = await NewService(db).ResetPasswordAsync(Guid.NewGuid(), new ResetPasswordRequest("NewPassw0rd!", Convert.ToBase64String(new byte[8])));
        Assert.Null(result);
    }

    [Fact]
    public async Task ResetPasswordAsync_StaleExpectedVersion_RejectedWithConflict()
    {
        await using var createDb = PostgresFixture.CreateContext();
        var created = await NewService(createDb).CreateAsync(new CreateUserRequest($"user-{Guid.NewGuid():N}", "Name", $"{Guid.NewGuid():N}@bpm-tests.local", "OldPassw0rd!", null));
        await NewService(createDb).UpdateAsync(created.Id, new UpdateUserRequest("Renamed", created.Email, null, true, created.RowVersion));

        await using var db = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<ConflictAppException>(() =>
            NewService(db).ResetPasswordAsync(created.Id, new ResetPasswordRequest("NewPassw0rd!", created.RowVersion)));
        Assert.Equal("USER_CONCURRENCY_CONFLICT", ex.Code);
    }

    [Fact]
    public async Task ResetPasswordAsync_TooShort_RejectedByValidation()
    {
        await using var createDb = PostgresFixture.CreateContext();
        var created = await NewService(createDb).CreateAsync(new CreateUserRequest($"user-{Guid.NewGuid():N}", "Name", $"{Guid.NewGuid():N}@bpm-tests.local", "OldPassw0rd!", null));

        await using var db = PostgresFixture.CreateContext();
        await Assert.ThrowsAsync<FluentValidation.ValidationException>(() =>
            NewService(db).ResetPasswordAsync(created.Id, new ResetPasswordRequest("short", created.RowVersion)));
    }

    [Fact]
    public async Task ResetPasswordAsync_WritesAuditEntry_WithoutPasswordMaterial()
    {
        await using var createDb = PostgresFixture.CreateContext();
        var actingAdminId = Guid.NewGuid();
        var created = await NewService(createDb).CreateAsync(new CreateUserRequest($"user-{Guid.NewGuid():N}", "Name", $"{Guid.NewGuid():N}@bpm-tests.local", "OldPassw0rd!", null));

        await using var resetDb = PostgresFixture.CreateContext();
        var service = new UserService(resetDb, new PasswordHasher<User>(), new AuditService(resetDb, new FixedCurrentUser(actingAdminId)), new CreateUserRequestValidator(), new UpdateUserRequestValidator(), new ResetPasswordRequestValidator(), new ChangePasswordRequestValidator());
        await service.ResetPasswordAsync(created.Id, new ResetPasswordRequest("NewPassw0rd!", created.RowVersion));

        await using var auditDb = PostgresFixture.CreateContext();
        var log = await auditDb.AuditLogs.SingleAsync(a => a.Action == "ResetPassword" && a.EntityId == created.Id.ToString());
        Assert.Equal(actingAdminId, log.UserId);
        Assert.Null(log.OldValue);
        Assert.Null(log.NewValue);
    }

    // ---- Phase 9: self-service change password ----

    [Fact]
    public async Task ChangePasswordAsync_CorrectCurrentPassword_Succeeds()
    {
        await using var createDb = PostgresFixture.CreateContext();
        var hasher = new PasswordHasher<User>();
        var created = await NewService(createDb).CreateAsync(new CreateUserRequest($"user-{Guid.NewGuid():N}", "Name", $"{Guid.NewGuid():N}@bpm-tests.local", "OldPassw0rd!", null));

        await using var changeDb = PostgresFixture.CreateContext();
        await NewService(changeDb).ChangePasswordAsync(created.Id, new ChangePasswordRequest("OldPassw0rd!", "NewPassw0rd!"));

        await using var verifyDb = PostgresFixture.CreateContext();
        var storedUser = await verifyDb.Users.SingleAsync(u => u.Id == created.Id);
        Assert.Equal(PasswordVerificationResult.Success, hasher.VerifyHashedPassword(storedUser, storedUser.PasswordHash, "NewPassw0rd!"));
        Assert.Equal(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(storedUser, storedUser.PasswordHash, "OldPassw0rd!"));
    }

    [Fact]
    public async Task ChangePasswordAsync_WrongCurrentPassword_Rejected_PasswordUnchanged()
    {
        await using var createDb = PostgresFixture.CreateContext();
        var hasher = new PasswordHasher<User>();
        var created = await NewService(createDb).CreateAsync(new CreateUserRequest($"user-{Guid.NewGuid():N}", "Name", $"{Guid.NewGuid():N}@bpm-tests.local", "OldPassw0rd!", null));

        await using var db = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<BadRequestAppException>(() =>
            NewService(db).ChangePasswordAsync(created.Id, new ChangePasswordRequest("WrongPassword!", "NewPassw0rd!")));
        Assert.Equal("INVALID_CURRENT_PASSWORD", ex.Code);

        await using var verifyDb = PostgresFixture.CreateContext();
        var storedUser = await verifyDb.Users.SingleAsync(u => u.Id == created.Id);
        Assert.Equal(PasswordVerificationResult.Success, hasher.VerifyHashedPassword(storedUser, storedUser.PasswordHash, "OldPassw0rd!"));
    }

    [Fact]
    public async Task ChangePasswordAsync_NonexistentUser_ThrowsNotFound()
    {
        await using var db = PostgresFixture.CreateContext();
        await Assert.ThrowsAsync<NotFoundAppException>(() =>
            NewService(db).ChangePasswordAsync(Guid.NewGuid(), new ChangePasswordRequest("whatever", "NewPassw0rd!")));
    }

    private class FixedCurrentUser : ICurrentUserService
    {
        public FixedCurrentUser(Guid? userId) => UserId = userId;
        public Guid? UserId { get; }
        public Guid TenantId => Guid.Empty;
        public IReadOnlyCollection<string> Roles => Array.Empty<string>();
        public string? IpAddress => null;
        public string? UserAgent => null;
    }
}
