# Changelog

## Unreleased

### Fixed
- `RowVersion` optimistic concurrency columns failed to insert on PostgreSQL (`23502 NOT NULL
  violation` on the very first seed write) because `.IsRowVersion()` assumes DB-generated values,
  which Npgsql doesn't provide. Switched to an application-managed token stamped in
  `BpmDbContext.SaveChanges(Async)`.
- `docker-compose.yml` referenced `minio/minio:latest`, which Docker Hub now rejects; switched to
  `quay.io/minio/minio:latest`.

### Added
- Initial solution scaffold: `BPM.Api`, `BPM.Application`, `BPM.Domain`, `BPM.Infrastructure`,
  `BPM.Workflow`, `BPM.Identity`, `BPM.Notification`, `BPM.Tests`.
- Domain model for Phase 1: `User`, `Organization`, `Department`, `Role`, `UserRole`, `AuditLog`.
- JWT-based authentication (`POST /api/auth/login`) and role-based authorization.
- CRUD APIs for Users, Organizations, Departments, Roles (admin-only writes).
- Read-only Audit Log query API.
- EF Core + PostgreSQL persistence, initial migration.
- Docker Compose stack (postgres, redis, minio, bpm-api, bpm-web placeholder).
- Bootstrap DB seeding (Administrator role + `admin` account) on API startup.
- xUnit test project with initial coverage for JWT token generation.
