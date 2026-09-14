using BPM.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace BPM.Tests.Workflow;

// Integration tests run against a real PostgreSQL database rather than mocks (Skill.md §27: the
// workflow engine leans on transactions/constraints/concurrency, which a mock can't exercise
// honestly). This targets the same Postgres the repo's docker-compose brings up
// (`JWT_SECRET=... docker compose up -d postgres`), in a separate "bpm_test" database so it never
// touches dev/seed data. If that Postgres isn't reachable, these tests fail loudly rather than
// silently skipping — per the project's "do not fake verification" rule.
public class PostgresFixture : IAsyncLifetime
{
    private const string AdminConnectionString = "Host=localhost;Port=5432;Database=bpm;Username=bpm;Password=bpm";
    public const string TestConnectionString = "Host=localhost;Port=5432;Database=bpm_test;Username=bpm;Password=bpm";

    public async Task InitializeAsync()
    {
        await using (var adminConnection = new NpgsqlConnection(AdminConnectionString))
        {
            await adminConnection.OpenAsync();
            var exists = await new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = 'bpm_test'", adminConnection).ExecuteScalarAsync();
            if (exists is null)
            {
                await using var create = new NpgsqlCommand("CREATE DATABASE bpm_test", adminConnection);
                await create.ExecuteNonQueryAsync();
            }
        }

        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public static BpmDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<BpmDbContext>()
            .UseNpgsql(TestConnectionString)
            .Options;
        return new BpmDbContext(options);
    }
}

[CollectionDefinition("Postgres")]
public class PostgresCollection : ICollectionFixture<PostgresFixture>
{
}
