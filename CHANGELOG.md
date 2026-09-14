# Changelog

## Unreleased

### Added — Phase 3: Approval Engine
- Domain: `ApprovalInstance` and `ApprovalAssignment` — a 1:1/1:N runtime companion to
  `TaskInstance` for `ApprovalTask` nodes (plain `UserTask` is unchanged).
- `AssignmentResolver` (`BPM.Workflow.Engine`): resolves User/Role/Department/DepartmentManager/
  ProcessInitiator assignments to concrete user ids server-side.
- `ApprovalEngine` (`BPM.Workflow.Engine`, internal): Approve/Reject/Return/Delegate/Transfer/
  AddApprover, implementing Sequential/All/AnyOne approval policies. Shares transition logic with
  `WorkflowEngine` via a new `WorkflowTransitions` helper rather than duplicating it.
- New API: `POST /api/tasks/{id}/approve`, `/reject`, `/return`, `/delegate`, `/transfer`,
  `/approvers` — mutually exclusive with `/complete` per task (enforced server-side).
- `TaskDto.Approval` (nullable `ApprovalSummaryDto`) exposes per-assignment approval state; `GET
  /api/tasks` now also includes tasks where the caller holds or was delegated an approval slot.
- Reuses Phase 2's `RowVersion` optimistic concurrency mechanism (no new concurrency approach) and
  the same state-check idempotency pattern.
- New audit actions: `ApprovalAssigned/Approved/Rejected/Returned/Delegated/Transferred`,
  `ApproverAdded`, `ApprovalCompleted`.
- Migration `AddApprovalEngine`; 33 new tests (9 pure validator unit tests, 24 integration tests
  against live Postgres) — 57/57 passing total, zero regressions in Phase 2's 24.
- Verified live: all three Skill.md §38 acceptance scenarios (Purchase Request sequential-node
  approval with Reject/Return/Transfer, Expense Request All-policy across three roles plus a
  live AddApprover, Leave Request AnyOne-policy across three named users).

### Fixed — Phase 3
- `ApprovalEngine.ResolveActingAssignment` crashed with an unhandled `InvalidOperationException`
  (`SingleOrDefault` on more than one match) when a user was simultaneously a direct approval
  candidate on their own assignment and the delegate for a different assignment on the same
  `ApprovalInstance`. Found during live testing (Return + Delegate against a user who already
  held the relevant role from earlier test data), not by the test suite first. Fixed to prefer
  the direct match, falling back to the earliest-order delegate match.

### Added — Phase 2: Workflow Core
- Domain: `ProcessDefinition`, `ProcessVersion` (immutable once published), `ProcessInstance`,
  `TaskInstance`; workflow graph POCOs (`BPM.Domain.Workflow.WorkflowDefinition`) serialized as
  JSON onto `ProcessVersion.DefinitionJson`.
- `WorkflowDefinitionValidator` (`BPM.Workflow`): fail-closed publish-time validation (start/end
  nodes, reachability, orphans, duplicate ids, unsupported node/assignment types, single-outgoing
  transitions per node).
- `WorkflowEngine` (`BPM.Workflow.Engine`): publish, start-process, and complete-task, each a
  single atomic `SaveChangesAsync` covering instance/task/audit-log changes together.
- Optimistic-concurrency task completion (409 on a losing concurrent complete) and
  state-check idempotency (409 on completing an already-completed task).
- Backend-authoritative task authorization: direct-user or role-based assignment, both enforced
  server-side (verified against real IDOR attempts).
- New API surface: `/api/process-definitions` (+ `/versions`, `/publish`), `/api/process-instances`,
  `/api/tasks` (+ `/complete`).
- Uniform `{code, message, traceId}` error responses via a new `AppException` hierarchy +
  `ErrorHandlingMiddleware` — applies to all endpoints, not just the new workflow ones.
- FluentValidation wired in for real (`CreateProcessDefinitionRequestValidator`,
  `CreateProcessVersionRequestValidator`, `StartProcessRequestValidator`), closing the Phase 1 gap
  where the package was installed but unused.
- Migration `AddWorkflowCore`; 24 tests (12 pure validator unit tests, 12 integration tests against
  a live Postgres, including a real optimistic-concurrency race and a real rollback proof).
- `README.md` (didn't exist before) with getting-started instructions and a full workflow example.

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
