# Phase 13 — Release Readiness Checklist

Scope: answer "is the BPM Platform a Release Candidate?" Not a new-feature phase. This document
records what was checked, what was found, what was fixed, and — just as importantly — what could
**not** be checked in this session's sandbox, stated plainly rather than assumed or faked.

## A. Product Scope

Phases 1–12 (Foundation through Performance & Scalability) are implemented, per `CLAUDE.md`'s own
running summary and `CHANGELOG.md`. Phase 13 adds exactly one new capability — bilingual UI
(zh-TW/en-US) — plus a verification pass and documentation cleanup. No new backend capability, no
architecture change, no new infrastructure (Redis, CQRS, event bus, microservices — all explicitly
out of scope per this phase's own brief, and none were touched).

## B. Functional Coverage

Every module listed in the brief's User Journey Audit exists in the codebase and was traced
against its actual source (not assumed from documentation): Login → Administration (Users/
Organizations/Departments/Roles/SLA Policies/Audit/Operational Health) → Process Definition/
Version → Form Designer → Workflow Designer → Validate → Publish → Start Process → Form Runtime →
Submit → Approval (Approve/Reject/Return/Delegate/Transfer/Add-Approver) → SLA → Notification →
My Tasks → Monitoring → Reporting → Analytics → Audit → Process Governance (Suspend/Archive/
Restore). Every one of these has a real backend controller/service and a real frontend page — none
are placeholders (`ComingSoon.tsx` exists as a component but nothing currently routes to it).

## C. Authorization / Security Acceptance

Re-verified directly against current source this session (not merely re-read documentation):

- `ProcessInstanceQueryService.VisibleProcessInstanceIdsAsync` / `TaskQueryService
  .BuildMyTaskIdsQuery` — both `internal static`, reused by `GetAllAsync`/`GetByIdAsync` and by
  Dashboard/Process Monitoring/Reporting/Analytics — confirmed still the single implementation of
  "which records can this caller see," never duplicated.
- 19 of 20 API controllers carry an `[Authorize]` attribute; the one exception is
  `AuthController` (the login endpoint itself — expected to be anonymous).
- `NotificationsController`/`AttachmentsController` resolve the caller via
  `ICurrentUserService.RequireUserId()` only — no `{userId}` route parameter or body field exists
  for either, so a caller cannot request another user's notifications/attachments by ID.
- Frontend hiding (`AdminRoute.tsx`) is documented in its own code comment as UX only; the backend
  `[Authorize(Roles = "Administrator")]` on every Administration-mutating endpoint (and the
  controller-level gate on `RolesController`/`AuditLogsController`/`SlaPoliciesController`/
  `EscalationPoliciesController`) is what's actually authoritative — confirmed present on all of
  them.
- Language switching is a pure frontend `localStorage` preference (`bpm-language`) — it sends
  nothing to the backend and cannot affect any authorization decision. Confirmed by reading
  `LanguageContext.tsx` in full: no network call anywhere in the file.

**Not independently re-run this session**: a live cross-user IDOR attack script against a running
API (would require Docker/Postgres — unavailable, see section N). The above is source-level
verification that the code path *would* reject such an attempt, consistent with Phase 5.4.4's/
5.5.3-5.5.4's own prior live-tested findings, not a fresh live reproduction.

## D. Workflow / Approval / Form Acceptance

Source-level re-check, not re-implementation:
- `WorkflowDefinitionValidator`'s fail-closed publish checks (single Start, ≥1 End, no orphans,
  full reachability, no duplicate ids, no unsupported node/assignment types) — unchanged, present.
- `WorkflowEngine.PublishVersionAsync` snapshots `FormReference.FormVersionId` at publish time —
  confirmed present (`WorkflowEngine.cs` lines ~100–106) and `FormReference` still carries the
  nullable `FormVersionId` field with its original doc comment.
- `ApprovalEngine`'s Sequential/All/AnyOne policies and the Return-vs-Reject distinction —
  unchanged (this phase touched zero files under `BPM.Workflow`).
- The plain-`UserTask`-vs-`ApprovalTask` mutual exclusion (`TASK_IS_APPROVAL_TASK`/
  `TASK_NOT_APPROVAL_TASK`) — unchanged.

## E. SLA Acceptance

`SlaSchedulerWorker` confirmed registered via `AddSlaScheduler()` in `BPM.Workflow
/DependencyInjection.cs`, called from `Program.cs`. `TaskSlaStatus.Overdue`, the
`WarningNotifiedAt`/`OverdueAt`/`EscalatedAt` idempotency fields, and `SlaEngine
.CompleteIfActiveAsync`'s `Active || Overdue` guard — all unchanged (zero files touched under
`BPM.Workflow/Engine/Sla*` or `BPM.Domain/Entities/*Sla*` this session).

## F. Notification Acceptance

`EmailDeliveryWorker` confirmed registered via `AddHostedService<EmailDeliveryWorker>()` in
`BPM.Notification/DependencyInjection.cs`. `WorkflowTransitions.AddNotificationWithDelivery`'s
"stage a Pending row, never check `EmailSettings` itself" design — unchanged. The frontend
`NotificationBell.tsx` was translated (Group D of the bilingual work) with zero change to its
polling behavior or API calls.

## G. Governance Acceptance

`ProcessDefinitionService.SuspendAsync`/`ArchiveAsync`/`RestoreAsync`'s `INVALID_LIFECYCLE_TRANSITION`
guards re-confirmed present with their original status-transition rules
(`Published→Suspended`, `{Published,Suspended}→Archived`, `{Suspended,Archived}→Published`).
`EnsureGovernanceAuthorized`'s Administrator-or-Owner check — unchanged.

## H. Reporting / Analytics Acceptance

No files under `BPM.Application/Reports`, `BPM.Application/Analytics`, or their `Infrastructure`
service implementations were touched this session. The Phase 12 performance fixes (2N+1 query
patterns, safe-pagination clamps) stand as last verified in that phase's own live-measured
benchmarks — see section O for why they were not re-measured live this session.

## I. Attachment Acceptance

`AttachmentsController`'s `[Authorize]` + `RequireUserId()` pattern and `AttachmentService`'s
Phase 10 hardening (structured `ATTACHMENT_STORAGE_UNAVAILABLE`/`ATTACHMENT_CONTENT_UNAVAILABLE`/
etc. errors, never a raw exception) — confirmed present, unchanged.

## J. Bilingual UI

- **zh-TW** (default) and **en-US** — both fully populated dictionaries,
  `frontend/src/i18n/locales/{zh-TW,en-US}.ts`, kept in lockstep by TypeScript's own excess-property
  checking (`en-US.ts`'s `const enUS: TranslationDictionary = {...}` — a key present in one but
  not the other is a compile error, not a silent runtime gap).
- **Language switch**: `AppLayout.tsx`'s header, next to the user menu (Global icon + current
  language label, a `Dropdown` with both options) — updates the whole current page immediately via
  React context, no reload, no re-login, no backend call.
- **Persistence**: `localStorage['bpm-language']`, read once at `LanguageProvider` mount, written
  on every `setLanguage` call. Falls back to `zh-TW` if the stored value is missing, corrupted, or
  `localStorage` itself throws (private browsing, blocked site data) — all three cases covered by
  dedicated tests in `LanguageContext.test.tsx`.
- **Ant Design locale**: `App.tsx`'s `ConfigProvider` `locale` prop switches between
  `antd/locale/zh_TW` and `antd/locale/en_US` based on the same language state — covered by
  `AntdLocale.test.tsx` (DatePicker's "今天"/"Today" label switching live).
- **Missing-translation fallback**: `t()` falls back to the other language, then to the literal
  dot-path key, logging a `console.warn` in dev only — never `undefined`, never a blank render,
  never a crash. Covered by a dedicated test.
- **Coverage**: all 65 user-visible `.tsx` components under `components/`, `pages/`, `features/`
  now source their text through `t()`. Confirmed by `grep -L useTranslation` returning exactly 2
  files (`ComingSoon.tsx`, `ProtectedRoute.tsx`), both verified to have zero hardcoded user-visible
  text of their own.
- **Backend semantics never translated**: error codes, enum values (task/process/approval status,
  node types, condition operators), audit action identifiers, RowVersion/ExpectedVersion field
  names — confirmed by spot-checking the Designer/PropertiesPanel files specifically (the highest-
  risk area for this rule, since they render raw enum values in dropdowns/tags) — Select `option`
  values, Tag contents, and JSON payloads sent to the backend all remain the original English enum
  literal; only the *displayed label* around them is translated.
- **Tests**: 12 dedicated i18n tests (`LanguageContext.test.tsx` ×9, `AntdLocale.test.tsx` ×3) plus
  the full existing suite re-passing after the translation wiring — 374/374, run 3 consecutive
  times clean.

## K. README

- Rewritten primarily in Traditional Chinese; technical terms (Workflow, SLA, Docker Compose,
  PostgreSQL, MinIO, etc.) and all code/CLI/route identifiers kept in their original English form
  per the phase's own §26/§27/§28 instructions.
- 26 sections matching the brief's requested structure (project intro through License/Project
  Information, plus a documentation index).
- **Cross-checked against actual source, not written from memory**: the Swagger-in-Production
  warning (§9) verified against `Program.cs`'s `app.Environment.IsDevelopment()` gate and
  `docker-compose.yml`'s hardcoded `ASPNETCORE_ENVIRONMENT: Development`; the "no restart policy"
  claim verified by grepping `docker-compose.yml` for `restart:` (zero matches); the JWT fail-fast
  claim verified against the `${JWT_SECRET:?...}` compose syntax; the Redis-unused claim consistent
  with `DEPLOYMENT.md`'s own Phase 10 Discovery note. §22 (language switching) was rewritten this
  session once bilingual coverage was actually complete, replacing an earlier accurate-at-the-time
  "partial coverage" note.
- Consistency check against `CHANGELOG.md`/`DEPLOYMENT.md`: no contradictory claims found; ports,
  environment variables, and Docker service names match `docker-compose.yml` exactly.

## L. Deployment Acceptance

**Verified by direct source inspection** (Docker itself was unavailable — see section N):
- 5 services (`postgres`, `redis`, `minio`, `bpm-api`, `bpm-web`) defined; `postgres`/`minio` have
  real healthchecks; `bpm-api` depends on both via `condition: service_healthy`.
- `JWT_SECRET` fails fast with no insecure default (`${JWT_SECRET:?JWT_SECRET must be set...}`).
- `ASPNETCORE_ENVIRONMENT: Development` is hardcoded in the committed compose file — Swagger is
  gated behind `IsDevelopment()`, so an unmodified deploy of this exact file would expose it. This
  is accurately documented as a deployer action item in README §9/DEPLOYMENT.md, not silently
  left implicit — **this is the one item closest to a release blocker**, addressed by documentation
  rather than a code change, since overriding `ASPNETCORE_ENVIRONMENT` for a real deployment is
  normal deployment practice (this repo's own compose file is explicitly a dev/local convenience,
  per `DEPLOYMENT.md`'s own "Prerequisites" framing) and no `appsettings.Production.json` exists to
  make a silent default meaningfully safer without deployer-supplied real secrets anyway. See
  section R for why this is classified Known Limitation, not Release Blocker.
- No `restart:` policy on any of the 5 services — documented as a deployer action item, not fixed
  (adding one project-wide is an infrastructure-policy decision the brief's own "no new monitoring
  subsystem / minimal infra changes" framing argues against making unilaterally).
- **Not performed this session**: an actual `docker compose up` from a clean environment, a rebuild
  of `bpm-api`/`bpm-web` images, or confirmation that the containers report healthy — no Docker
  daemon is reachable in this sandbox (`docker ps` fails with "cannot connect to the Docker API").
  The last live-verified Docker state is Phase 12's own record (all 5 containers healthy, both
  background workers ticking after a rebuild) — see `PROGRESS.md`'s Phase 12 section.

## M. Backup / Restore

`DEPLOYMENT.md`'s Backup/Restore section (pg_dump/pg_restore + mc mirror procedure, verification
checklist, cadence recommendation) is unchanged this session and was not re-touched — it was
already a complete, accurate operational procedure document from Phase 10. **A live backup/restore
drill was not performed this session** — it requires a running PostgreSQL/MinIO, unavailable in
this sandbox (see section N). This is a real, stated gap in this session's own verification, not
a claim of "validated in isolated environment" that the original Phase 13 brief asked for.

## N. Testing Limitations (read before trusting any "passes" claim in this document)

This sandbox has **no Docker daemon** (`docker ps` → "failed to connect to the Docker API... The
system cannot find the file specified") and **no reachable PostgreSQL/MinIO** (a bare `dotnet
test` run: 148 of 150 tests in a non-Workflow-namespace slice failed at the connection step,
`Npgsql.NpgsqlException: Failed to connect to 127.0.0.1:5432`, `SocketException: 無法連線`). This
project's own established architecture is that nearly every backend test is a live-Postgres
integration test (`PostgresFixture`) — there is no meaningful "unit-test-only" subset that proves
correctness without it.

What **was** verified this session:
- `dotnet build` (whole solution): 0 warnings, 0 errors.
- `npx tsc -b --noEmit` (frontend): 0 errors.
- `npm run build` (frontend production build): succeeds.
- `npm test -- --run` (frontend, Vitest + RTL): **374/374 passing, 38/38 files — run 3 consecutive
  times, all clean**, meeting the brief's own "3 consecutive clean runs" requirement for the one
  test suite this sandbox could actually run.

What was **not** re-run this session, stated explicitly rather than assumed or inflated:
- Backend test suite (`dotnet test`) — blocked by no reachable Postgres.
- Live HTTP test suite (`npm run test:live`) — blocked by no running `bpm-api`.
- Docker rebuild/health verification.
- Backup/restore drill.
- Browser-based UAT (see section Q).

The last point at which Backend/Live tests **were** actually run and passing is Phase 12's own
record: **Backend 492/492, Live 42/42**, 3 consecutive clean runs, against a real rebuilt Docker
stack. This session's changes are entirely frontend-only (`frontend/src/**` plus documentation) —
zero files under `src/BPM.*` were modified — so there is no code-level mechanism by which this
session's work could regress the backend or live suites. That is a reasoned expectation based on
the diff's actual shape, not a substitute for actually re-running them, and the next session with
Docker/Postgres available should do so before treating this as fully confirmed.

## O. Performance Regression

Not re-measured live this session (no running API — see section N). Zero backend files were
touched, so Phase 12's own live-measured baseline
(`/api/analytics/overview` ~0.03–0.05s, `/api/tasks` paginated ~8ms) has no code-level reason to
have regressed. The frontend production bundle still builds to one ~1.78MB/~584KB-gzip chunk
(unchanged from Phase 12's own finding — code-splitting remains deliberately deferred, not touched
this phase) plus ~19KB of CSS — no meaningfully different bundle-size profile from the new
translation dictionaries (~800 short string entries, well under any size concern).

## P. Security Regression

No backend authorization/authentication file was modified this session (confirmed: `git status`
equivalent — every backend change in the working tree predates this session's own work; this
session's diffs are entirely under `frontend/src/**`, plus `README.md`/`CHANGELOG.md`/`PROGRESS.md`/
this file). Language switching cannot alter authorization behavior by construction — see section C.

## Q. Browser Verification

**Unavailable.** The Claude-in-Chrome extension was not connected in this session — consistent
with every prior phase's own documented finding (`PROGRESS.md`'s Phase 12/7.2.3/etc. entries). No
browser-based UAT of the 16-step journey the brief requested (Login → language switch → every major
page → switch back → refresh → confirm persistence) was performed. This is stated explicitly per
the brief's own §39 instruction, not glossed over. The frontend behavior claimed in section J above
is verified by RTL-rendered component tests (a real DOM, real AntD components, real user-event
interactions in jsdom) — a meaningfully strong signal, but not the same as an actual browser.

## R. Release Blockers

**None identified**, using the brief's own narrow definition (application cannot start,
authentication broken, authorization bypass, data corruption, workflow/approval cannot complete,
historical data corrupted, production deployment unsafe *with no available mitigation*, restore
impossible, a critical UI flow unusable, or bilingual switching crashing the application). Every
candidate considered was found to already have a stated, actionable mitigation (see section L's
Swagger/restart-policy discussion) or to be outside what this session could evaluate at all (Docker
rebuild, live backup/restore) rather than evaluated-and-failing.

## S. Non-Blocking Issues

- `docker-compose.yml`'s hardcoded `Development` environment and absent `restart:` policies — both
  require deployer action before a production deploy; both are already documented in README §9/
  DEPLOYMENT.md. Not fixed this session (would be a compose-file/infrastructure-policy change
  outside this phase's own "no new infrastructure" instruction, and the correct override is
  environment-specific — a `docker-compose.override.yml` this repo doesn't have an established
  pattern for yet).
- The five text/aria-label drift bugs found and fixed during bilingual wiring (section J /
  CHANGELOG's "Fixed — Phase 13") — all fixed this session, listed here only for completeness of
  the audit trail.

## T. Deferred Features (confirmed still deferred, not silently dropped)

Redis usage, CI/CD pipeline, MFA, token revocation, `ProcessPermission` fine-grained permissions,
External API/Webhook/Import-Export integration, keyset pagination, `pg_trgm` fuzzy search
indexing, High Availability — all confirmed still absent from the codebase this session (no new
code introduces any of them), matching README §24's own Known Limitations list. None were added,
none were removed.

## U. Final Release Recommendation

**No Release Blocker identified within what this session's sandbox could evaluate.** The system's
functional surface (Phases 1–12), its authorization architecture, and its historical-integrity
guarantees were all re-inspected against current source this session and found consistent with
their documented design — no drift found anywhere checked. Bilingual UI is complete, tested, and
does not touch backend behavior.

This recommendation carries one honest, load-bearing caveat: **this session could not run the
backend test suite, the live HTTP test suite, a Docker rebuild, or a backup/restore drill**,
because this sandbox has no Docker daemon and no reachable PostgreSQL/MinIO. The last point those
were actually verified is Phase 12 (Backend 492/492, Live 42/42, Docker healthy, 3 consecutive
clean runs). This session's own changes are frontend-only, so there is no code-level reason to
expect regression in any of those — but "no reason to expect a problem" is a different, weaker
claim than "confirmed passing," and this document does not conflate the two. **Before an actual
production release, a session with Docker/PostgreSQL/MinIO available should re-run the full
Backend + Live suites (3 consecutive clean runs), rebuild and health-check the Docker stack, and
perform one real backup/restore drill in an isolated environment** — none of which this session
could do, not because they were skipped by choice.

---

PHASE 13 FINAL PRODUCT & RELEASE READINESS COMPLETE

RELEASE CANDIDATE STATUS: NO RELEASE BLOCKER IDENTIFIED (within this session's sandbox — see
section U's caveat on backend/live/Docker/backup-restore verification not being re-runnable here).
