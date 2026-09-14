# Changelog

## Unreleased

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
