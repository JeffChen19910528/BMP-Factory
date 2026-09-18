# Changelog

## Unreleased

### Added — Phase 13: Final Product & Release Readiness + Bilingual UI
A release-candidate audit phase, not a new-feature phase — the only new capability shipped is
bilingual UI; everything else is verification, a small number of real UI-text regressions found
and fixed during that verification, and documentation brought in line with actual behavior.
- **Bilingual UI (Traditional Chinese `zh-TW` default / English `en-US`)**: a small,
  dependency-free i18n system (`frontend/src/i18n/LanguageContext.tsx` — no `react-i18next`/
  `i18next`/`react-intl` added; `package.json` gained zero new runtime dependencies for this).
  `useTranslation()` exposes `{ language, setLanguage, t, translateErrorCode }`; `t('a.b.c')` looks
  up a dot-path key, falling back to the other language and then to the raw key path if truly
  missing (a `console.warn` in dev, never a crash or a blank render). All 65 user-visible `.tsx`
  components under `components/`, `pages/`, and `features/` now source their text through this —
  navigation, every Administration page and its Create/Edit modals, the Process/Form Designers'
  canvases, Form Runtime, Task Detail's full Approve/Reject/Return/Delegate/Transfer/Add-Approver
  action set, Approvals Worklist, Monitoring/Reporting/Analytics, Audit, Notifications, and all
  shared confirm dialogs/empty states/validation messages. Two components have no user-visible
  text of their own (`ComingSoon.tsx`, `ProtectedRoute.tsx`) and were correctly left untouched.
  Backend semantics (error codes, enum values, audit action identifiers, workflow/approval/task
  status values) are never translated — only the human-readable label shown around them; error
  display resolves a backend `{code, message, traceId}` through `translateErrorCode(code)` and
  falls back to the backend's own `message` when no localized text exists for that code yet.
- Language preference persists to `localStorage` (`bpm-language`) — a pure client-side UI
  preference, never written to the database or `User` entity, never sent to the backend, and never
  changes any authorization/business-rule behavior.
- Ant Design's own component locale (DatePicker/Table/Pagination/Form validation text) now tracks
  the selected language via `ConfigProvider`'s `locale` prop in `App.tsx`.
- 12 new frontend tests covering the i18n behavior itself (default language, switching both
  directions, persistence/restore across a simulated reload, missing-key fallback, corrupted
  stored-value fallback, localStorage-unavailable fallback, and AntD locale following the
  selected language) — deliberately behavior tests, not per-string snapshot tests.
- `README.md` rewritten primarily in Traditional Chinese (technical terms and all code/CLI/API
  identifiers kept in English), restructured into the 26 sections release documentation should
  cover (architecture, every feature module, deployment, backup/restore, language switching,
  testing, known limitations, browser-verification limitation). Content was checked against the
  actual repository (`docker-compose.yml`, `Program.cs`, controller `[Authorize]` attributes,
  domain code) rather than written from memory.
- New `docs/PHASE_13_RELEASE_READINESS.md` — the release-candidate checklist and findings for
  this phase.

### Fixed — Phase 13
Found only while wiring existing components to the translation system — none of these are new
bugs introduced by this phase's own review work, and none touch backend code or business logic:
- Several Create-entity buttons/modal OK buttons (Department/Organization/Role/User/Process) had
  been pre-translated with wording that silently drifted from the original hardcoded English text
  ("New X" instead of "Create X"; a shared `common.add` ("Add") key reused for a modal OK button
  that had always said "Create") — caught by 14 existing frontend test files asserting the exact
  original button text, fixed by correcting the translation dictionary values and adding a
  distinct `common.create` key rather than changing the tests or the actual UI wording.
- The Version Comparison card's heading and its "Compare Versions" action button had been
  collapsed onto a single translation key, changing the heading's text; given a dedicated
  `processes.versionComparisonTitle` key.
- A handful of aria-labels (rule-builder delete button, form-field move-up/move-down buttons, a
  required-field validation message) lost an exact word/punctuation match against their original
  text during translation; corrected in the dictionaries.
- 14 existing test files that render a component directly (not through the shared
  `renderWithProviders` test helper) needed an explicit `LanguageProvider` wrapper added now that
  `useTranslation()` throws outside one — a mechanical test-infrastructure update, not a behavior
  change.

### Verified — Phase 13 (no code change; see `docs/PHASE_13_RELEASE_READINESS.md` for detail)
- Authorization/IDOR: every `ProcessInstanceQueryService`/`TaskQueryService`
  authorization-predicate reuse, every controller's `[Authorize]` coverage, and
  `Notifications`/`Attachments` caller-identity resolution re-inspected directly against current
  source — matches this file's own documented architecture.
- Concurrency/idempotency: `BusinessKey` uniqueness + `23505` mapping, `RoleService.AssignAsync`'s
  same-class fix, and RowVersion/ExpectedVersion wiring on User/Organization/Role spot-checked
  against current source.
- Historical integrity: `FormReference.FormVersionId` publish-time snapshotting and the
  Suspend/Archive/Restore lifecycle guard's `INVALID_LIFECYCLE_TRANSITION` checks confirmed
  unchanged and correctly gated in current source.
- Both background workers (`SlaSchedulerWorker`, `EmailDeliveryWorker`) confirmed registered.
- Deployment configuration claims in `README.md`/`DEPLOYMENT.md` (Swagger gated behind
  `IsDevelopment()`, `docker-compose.yml`'s hardcoded `ASPNETCORE_ENVIRONMENT: Development`, no
  `restart:` policy on any of the 5 services, `JWT_SECRET` fail-fast) checked directly against
  `Program.cs` and `docker-compose.yml` — accurate as documented.
- **Not performed this session, and explicitly not claimed**: backend/live test re-runs (this
  sandbox has no reachable PostgreSQL/MinIO — `dotnet test` fails at the connection step for
  every Postgres-backed test, confirmed, not assumed), a Docker rebuild/health verification, a
  backup/restore drill, and browser-based UAT (no Claude-in-Chrome connection available). See
  `docs/PHASE_13_RELEASE_READINESS.md` for exactly what this means for release readiness.

### Fixed — Phase 12: Performance & Scalability
A narrow, evidence-driven performance pass following a dedicated discovery report — every change
here traces back to a real, measured bottleneck, not a speculative rewrite.
- **The Analytics dashboard's slowest view (Process Comparison, part of the Overview page) is now
  roughly 20-30x faster** — what used to take about 1.1 seconds now completes in well under
  100ms, confirmed by direct before/after measurement against the live system. The underlying
  cause was the same class of inefficient-query pattern already fixed once elsewhere in the
  Analytics module; the fix here is the identical, well-tested pattern applied a second time, not
  a redesign.
- Fixed four more places (Audit Logs, Process Definitions list, the Approvals Worklist) that
  shared the same rare pagination edge case already fixed once before elsewhere in the system —
  requesting an extreme, out-of-range page number could previously surface a raw server error
  instead of simply returning an empty page.
- **My Tasks (`GET /api/tasks`) is now paginated** — previously every task a user had ever been
  assigned, approved, or participated in was fetched in a single unbounded request. The frontend
  is unaffected (it still sees a normal task list); the change is entirely about not overfetching
  on the backend.
- Added a database index to speed up sorting a user's task list by creation date.
- The web app's data-fetching layer now uses sensible caching defaults (previously every screen
  refetched on every window focus) — reduces unnecessary network traffic without affecting
  screens that intentionally poll for live updates (e.g. the notification bell).

### Fixed — Phase 10: Production Hardening & Cross-Module Correctness
A hardening pass across the whole platform, not a new feature set — no new external integration
capability was added (an investigation phase concluded the system doesn't need one yet); instead
this closes real correctness, reliability, and consistency gaps found by a systematic review.
- Starting the same business transaction twice in quick succession (a double-click, or a network
  retry) can no longer create two separate process instances — an optional reference number
  (already supported when starting a process) is now enforced as unique per process, and a repeat
  attempt is rejected with a clear error instead of silently duplicating the work.
- Fixed a rare but real bug where two administrators assigning the same role to the same user at
  almost the same moment could get an unexpected server error instead of both actions simply
  succeeding.
- **Forms attached to a process step now stay consistent for the lifetime of that process
  version** — previously, republishing a form while a process was already published and running
  could cause new tasks on that process to unexpectedly pick up the newer form. A published
  process version now always uses the exact form version that was current when *it* was
  published, for as long as that process version exists — matching the guarantee already in place
  for the rest of the process definition.
- File attachments are more resilient to storage hiccups: a failed upload no longer leaves an
  orphaned file behind without cleanup, and a failed download or delete now returns a clear error
  instead of an unhandled server failure.
- The file storage service (MinIO) now has a proper startup health check, so the API waits for it
  to be ready before accepting file-upload traffic — closing a rare startup-timing gap.
- Application logs are now also written to durable, rotating log files (in addition to the
  console), so operational history survives a container restart.
- Fixed a performance issue in the Analytics page's workflow-step breakdown that could slow down
  as a process definition grew more steps.
- Added a deployment guide (`DEPLOYMENT.md`) covering configuration, health checks, and a
  documented backup/restore procedure for the database and file storage.

### Added — Phase 9: System Administration & Configuration
Administration workspace grows to cover Organizations, SLA Policies, password management, Role
renaming, and a read-only Operational Health view — filling the gaps a dedicated inspection pass
found in the existing Users/Departments/Roles/Audit Logs workspace.
- Organizations can now be edited (name, parent) with the same conflict protection and audit trail
  Departments already had; the Organizations list is a new tab in Administration.
- SLA Policies — configurable per process step since an earlier release, but with no screen to
  manage them until now — get a full Administration UI: create, view, and edit.
- An Administrator can reset any user's password directly (no email link, no waiting) — the new
  password is required immediately and is never shown or logged again after the reset.
- Every user can change their own password from the account menu, after confirming their current
  one — no administrator involvement needed for routine password changes.
- Roles can now be renamed by an Administrator; the built-in Administrator role itself is protected
  from being renamed away.
- User records now show when someone last successfully logged in.
- A new Operational Health page gives Administrators a single, read-only view of system status:
  database and file-storage connectivity, notification delivery backlog, and a handful of
  non-sensitive configuration values — never passwords, keys, or connection strings, and never a
  status that's just guessed to look healthy.
- Fixed a bug (found while load-testing this release, not reported by a user): logging in twice in
  quick succession as the same account could occasionally fail outright instead of both attempts
  succeeding. Login is now reliable under concurrent use.

### Added — Phase 8: Process Governance & Lifecycle
Suspend, Archive, and Restore actions for process definitions, an accountable Owner per process,
an optional Change Reason on publish, and a Version Comparison view — building entirely on the
existing lifecycle status field and query architecture.
- Suspending or archiving a process blocks new instances from starting while leaving every
  already-running instance, its tasks, approvals, and SLA tracking completely unaffected —
  restoring it makes it startable again without touching any of its published workflow versions.
- An Administrator can assign an accountable Owner to a process; that owner (or any Administrator)
  can then suspend, archive, or restore it — nobody else can, and ownership itself can only be
  changed by an Administrator.
- Publishing a new version can optionally record why it was published; that note is shown in the
  version history and can never be changed afterward, matching a published version's own existing
  immutability.
- A new version comparison view shows exactly what changed between two versions of the same
  process — steps added, removed, or modified, and how — without ever mixing up unrelated
  processes.
- Closes a pre-existing gap where a process's name/description could be edited regardless of its
  lifecycle state — a suspended or archived process must be restored first.
- Dashboard, Process Monitoring, Reporting, and Analytics all continue to show suspended/archived
  processes and their historical data exactly as before — none of them needed to change.

### Added — Phase 7.4: Analytics
An Analytics page — `GET /api/analytics/overview` — with historical trends and duration analytics
built entirely on the existing query architecture. **Phase 7.4 is done, and with it Phase 7 as a
whole is now COMPLETE** (Dashboard, Process Monitoring, Reporting, Analytics).
- Process volume/completion/rejection trend over time, process and task duration (average/min/max
  with sample size), a workflow-step ("node") breakdown showing where time is being spent, an SLA
  compliance trend, and a process-definition comparison table — all filterable by date range,
  process, status, initiator, and department, and computed from the same authorization scope as
  Dashboard/Process Monitoring/Reporting, so a user's analytics can never reveal more than their
  own authorized data.
- Historical accuracy: a workflow step's name always reflects the process version an instance
  actually ran on, even after a newer version renames that same step.
- Descriptive only — no predictions, no trend forecasting, no anomaly detection, no automated
  recommendations. Tables and summary numbers only, no charts.

### Added — Phase 7.3: Reporting
A complete Reporting page — `GET /api/reports/summary`, `/details`, `/export` — built entirely on
the existing Dashboard/Process Monitoring query architecture. **Phase 7.3 is done; Phase 7 overall
is not** (7.4 Analytics remains unstarted).
- Summary cards (Process/Task/Approval/SLA), a Process Breakdown table, and a paginated Detail
  table, all filterable by date range, process, status, initiator, and department, and all backed
  by real database-side aggregation — never loading every historical record into memory to count
  them.
- CSV export of the current filtered report, protected against spreadsheet formula-injection
  attacks and bounded to a safe maximum row count.
- Zero new authorization logic — reuses the exact same authorization computation Dashboard/Process
  Monitoring already use, applied *before* any number is aggregated, so a user's report totals can
  never reveal so much as a count of another user's data, even through a crafted filter or export
  request.
- SLA compliance is computed from the real, persisted SLA history (never recalculated from the
  current time or the current SLA policy) with an explicit, documented definition.
- This is a reporting page, not an analytics dashboard — tables and summary numbers only; no
  charts, trend lines, or forecasting.

### Fixed — Phase 7.2.3: Operational Monitoring Hardening
A review-and-harden pass over Process Monitoring's list and detail pages. **Phase 7.2 (Process
Monitoring) is now complete.**
- Fixed a bug where requesting an extremely large page number could return a server error instead
  of an empty result.
- Fixed an inconsistency where a task's SLA could show as "Active" on the Process Instance Detail
  page while correctly showing "Warning" on the Process Monitoring list and Dashboard for the same
  task — all three now always agree.
- Everything else in Process Monitoring (search, filters, sorting, pagination, authorization,
  timeline correctness, historical process version handling) was reviewed against real data and
  confirmed already working correctly — nothing else needed to change.

### Added — Phase 7.2.2: Process Instance Detail + Timeline
A read-only Process Instance Detail page — `GET /api/process-monitoring/{id}` — reachable from the
Process Monitoring list. **Phase 7.2.2 is done; Phase 7 overall is not** (7.2.3 Operational
Hardening, 7.3 Reporting, 7.4 Analytics remain unstarted).
- Shows the process summary, a workflow progress bar (completed/current/pending steps), the
  current task with its SLA, full task and approval history, and a chronological activity
  timeline — all read directly from the real engines, nothing computed or guessed on the frontend.
- Zero new authorization logic — reuses the exact same authorization check Process Monitoring
  already uses, so a user can only ever open a process instance they're genuinely authorized to
  see; an unrelated user's attempt is rejected the same way it always has been, before and after
  the process resolves.
- The activity timeline is a deliberately curated set of real, verified business events (process
  started, tasks created/completed, approval decisions, SLA warnings/overdue/escalations) — never
  raw internal engine logs, never duplicated events, always in a stable chronological order.
- Always reflects the exact process version an instance actually ran on — publishing a newer
  version of the same process definition later never changes what an existing instance's detail
  page shows.
- Read-only: never modifies a process, task, approval, or SLA record, and never writes an audit
  log entry just for viewing the page.

### Added — Phase 7.2.1: Process Monitoring Query + List
A real operational Process Monitoring list — `GET /api/process-monitoring` — replacing the
previous placeholder at the existing Process Instances page. **Phase 7.2.1 is done; Phase 7
overall is not** (7.2.2 Process Detail + Timeline, 7.2.3 Operational Hardening, 7.3 Reporting, 7.4
Analytics remain unstarted).
- Server-side search, status/SLA/initiator/date-range filtering, whitelisted sorting, and
  pagination — the database always does the filtering/sorting/counting, never the browser.
- Zero new authorization logic — reuses the exact same authorization computations
  Dashboard/Process Instances already use, so a normal user only ever sees their own authorized
  processes and an Administrator sees the system-wide list; no filter (including filtering by
  another user's id) can ever widen what a caller is allowed to see.
- Shows each process's current task (name, status, and a safe assignee display that never claims
  an arbitrary approval candidate) and current SLA state, both read directly from the real
  workflow/SLA engines — never recalculated or guessed on the frontend.
- Process status only ever shows what the engine actually reaches (Running/Completed/Rejected) —
  no invented "Returned" process-level status.
- Read-only: never modifies a process, task, approval, or SLA record, and never writes an audit
  log entry just for viewing the list.

### Added — Phase 7.1: Dashboard Foundation
A real BPM Operational Dashboard — one `GET /api/dashboard` endpoint aggregating My Tasks, Pending
Approvals, SLA Overview, Process Overview, and Recent Activity. **Phase 7.1 is done; Phase 7
overall is not** (7.2 Process Monitoring, 7.3 Reporting, 7.4 Analytics remain unstarted).
- Zero new authorization logic anywhere — every number is scoped by reusing this codebase's
  existing, already-tested authorization computations (which tasks/approvals/process instances a
  user can see), extracted into two small reusable methods rather than duplicated. Administrator
  sees a system-wide aggregate; everyone else sees exactly their own authorized data; a client
  cannot widen this by adding a `?userId=` query parameter — there is nothing on the endpoint that
  even accepts one.
- SLA Overview and "Overdue" task counts read only Phase 6.4's own persisted, scheduler-set SLA
  state — never a client-side or independently recomputed overdue/warning calculation.
- Process Overview reports the three process states the engine actually ever reaches
  (Running/Completed/Rejected) rather than a guessed or invented shape.
- Recent Activity surfaces a small, curated set of meaningful events (process started/completed/
  rejected, task completed, approval approved/rejected/returned, plus the caller's own SLA
  warning/overdue/escalation notifications) — never scheduler-polling noise, and never another
  user's activity a caller isn't authorized to see.
- Dashboard is now the default page after login; no existing page, route, or navigation item was
  removed or renamed.
- Read-only: opening the Dashboard never writes an audit log entry, never touches Phase 6's
  Notification/Email/SLA background workers, and required no database migration.

### Added — Phase 6.4: SLA Scheduler / Warning / Overdue / Escalation
Makes Phase 6.3's static SLA model operational. **Phase 6.4 is done — Phase 6 as a whole is now
COMPLETE.**
- A new `SlaSchedulerWorker`/`SlaProcessor` pair, wholly independent from `EmailDeliveryWorker` —
  it evaluates every Active `TaskSla`'s Warning/Due thresholds against a real clock, transitions
  `Active -> Overdue`, and creates `SlaWarning`/`SlaOverdue` notifications through the existing
  Notification architecture. `EmailDeliveryWorker` was not modified at all; the SLA scheduler
  never calls email directly and never touches `NotificationDelivery`.
- Warning and Overdue idempotency is 100% database-authoritative — three new persistent fields on
  `TaskSla` (`WarningNotifiedAt`, `OverdueAt`, `EscalatedAt`), each claimed via its own conditional
  database update so two scheduler instances racing on the same task can never double-notify or
  double-transition — verified with real concurrent workers against real PostgreSQL, not just
  sequential calls.
- A new, minimal `EscalationPolicy` — after a configurable delay past Overdue, escalates to one
  additional recipient (User/Role/DepartmentManager/ProcessInitiator, reusing the existing
  workflow assignment resolver, never a new recipient system or an arbitrary email address).
  Administrator-only configuration API, no escalation designer UI.
- Three new notification types — `SlaWarning`, `SlaOverdue`, `SlaEscalated` — flow through the
  existing, unmodified Email delivery pipeline with zero special-casing.
- Task Detail now shows `Overdue` as a real status (backend-set, never computed client-side).
- Fixed a genuine, previously-undiscovered bug found while investigating an intermittent live-test
  failure: `RoleService.CreateAsync` had no duplicate-name check at all (unlike User/Department),
  so a duplicate role name produced an unhandled `500` instead of a clean `409` — now fixed with a
  regression test. Separately tightened two live test files' name-uniqueness to be
  collision-resistant under this suite's parallel execution.

### Added — Phase 6.3: SLA Foundation
The foundational SLA model for BPM Tasks — `SlaPolicy` (admin-configured duration/warning-offset
per process step) → Task creation → deterministic calculation → `TaskSla` (Started/Warning/Due/
Completed/Status). **Phase 6.3 is done; Phase 6 overall is not** (6.4 SLA Scheduler/Escalation
remains unstarted — no time-based Warning/Overdue transition, no SLA-triggered notification, no
escalation exists yet).
- A new `SlaPolicy` entity, keyed on `(ProcessDefinitionId, NodeId)` rather than embedded in the
  published `WorkflowDefinition` JSON, so editing a policy never touches an already-published,
  immutable process version. Full RowVersion/ExpectedVersion optimistic concurrency, same pattern
  as User/Department/Role. Administrator-only CRUD (`GET`/`POST`/`PUT /api/sla-policies`).
- A new `TaskSla` entity — the per-task execution record (`StartedAt`/`WarningAt`/`DueAt`/
  `CompletedAt`/`Status`), deliberately kept separate from `TaskInstance` rather than adding SLA
  fields onto it directly. `TaskInstance.DueAt` (reserved-but-unused since Phase 2) is now
  populated as a read-only mirror of the authoritative `TaskSla.DueAt`.
- SLA calculation is deterministic and UTC-only (`SlaCalculator`, pure/testable, no `DateTime.Now`
  inside it) — a `TaskSla` is created automatically whenever a Task is created at a step with an
  enabled policy, and marked `Completed` automatically whenever that task is completed, approved,
  rejected, or returned. No `Cancelled` path exists yet (mirrors `TaskInstanceStatus.Cancelled`
  being equally unreachable today — no "cancel" capability exists anywhere in the engine).
- No new SLA-by-task API — `TaskDto` simply gained an optional `Sla` field, riding entirely on the
  existing, already-authorized `GET /api/tasks/{id}` (an unrelated user still gets `403`, exactly
  as before this phase).
- **No scheduler, no background worker, and no automatic time-based status transition were added.**
  `EmailDeliveryWorker` (Phase 6.2) was not modified — it remains the only `BackgroundService` in
  the codebase. A Task's SLA only ever changes on a real lifecycle event (created/completed), never
  on the passage of time — that is Phase 6.4's entire scope, not started.
- Task Detail now shows a small SLA panel (Status/Started/Warning At/Due At/Completed) when a task
  has one — no client-computed "Overdue" label; that requires an authoritative time-based status
  Phase 6.4 owns, not a frontend guess.
- Editing an `SlaPolicy` never rewrites history — an already-created `TaskSla` keeps the
  `WarningAt`/`DueAt` it was calculated with forever; only tasks created after the change see the
  new duration.

### Added — Phase 6.2: Notification Delivery (Email)
Extends Phase 6.1's In-App Notification Foundation with a real, asynchronous Email delivery path —
**Phase 6.2 is done; Phase 6 overall is not** (6.3 SLA Foundation, 6.4 SLA Scheduler/Escalation
remain unstarted).
- A new `NotificationDelivery` entity, deliberately separate from `Notification` (Notification =
  "who/what/should-they-see-it," unchanged from 6.1; NotificationDelivery = "how was this
  channel-specific attempt going") — `Channel`, `Status` (`Pending`/`Processing`/`Sent`/`Failed`),
  `RecipientAddress`, `AttemptCount`, `LastAttemptAt`, `SentAt`, `NextAttemptAt`, `LastError`.
  Every Notification now gets a paired, Pending Email delivery row automatically
  (`WorkflowTransitions.AddNotificationWithDelivery`), committed by the exact same single
  `SaveChangesAsync` the engine already used for the Notification itself in 6.1 — no new
  transaction boundary, no event bus.
- **Workflow/Approval never call email directly.** The engine only ever creates a Pending delivery
  row (it has zero knowledge of whether email is enabled, what SMTP is, or how to send anything);
  a separate background worker (`EmailDeliveryWorker`, a `BackgroundService`) and its testable core
  (`NotificationDeliveryProcessor`) do all the actual work — claim, resolve `User.Email`, render a
  template, call `IEmailSender`, record the outcome — entirely outside the HTTP request/business
  transaction that created the notification.
- **Business operations never roll back because email fails.** An approval, task completion, or
  process start all commit and return `200`/`201` regardless of what happens to email delivery
  afterward — proven live (a user with a simulated-always-failing address still gets a successful
  `201 Created` on process start).
- Concurrency-safe claiming without a new `RowVersion`: a single conditional `ExecuteUpdateAsync`
  per candidate row, re-checking the same eligibility predicate as part of the `UPDATE`'s own
  `WHERE` clause — two workers racing on the same row can't both claim it, and a worker that
  crashed mid-attempt (a stale `Processing` row past a lease timeout) is recoverable rather than
  lost.
- Bounded retry with linear backoff (`RetryBackoffSeconds x attemptCount`) up to
  `MaxAttempts`, then permanently `Failed` — verified live through an actual fail-then-succeed
  retry and a real exhausted-retries-ends-Failed run, not just unit-level.
- `IEmailSender` abstraction with two implementations selected by one config value
  (`Email:Provider`): `SmtpEmailSender` (production, built on .NET's own `SmtpClient`, no new
  NuGet dependency) and `FakeEmailSender` (this repo's own Docker Compose stack and local dev,
  since no real mail server is available — deterministic, not random, so retry/failure scenarios
  stay testable over real HTTP). Never both at once; a real deployment must explicitly opt into
  `Provider=Smtp` with real configured credentials — nothing is hard-coded, no secret is committed.
- Code-based email templates (`EmailTemplates`, selected by `NotificationType`, one per each of
  the 8 existing types) reusing the Notification's own already-backend-authored Title/Message as
  the body — no Template Designer, no database template table, no duplicated business text.
- A narrow, Administrator-only diagnostic endpoint, `GET /api/notifications/{id}/delivery` — the
  one justified exception to "no new delivery API"; every existing Phase 6.1 endpoint (`GET
  /api/notifications`, `GET /api/notifications/unread-count`, `POST /api/notifications/{id}/read`,
  `POST /api/notifications/read-all`) is unchanged and remains caller-scoped with no recipient
  parameter anywhere.
- Testing: 277 frontend tests unchanged (no frontend UI changes this phase — the existing
  NotificationBell already works unmodified). 271 backend tests (251 unchanged + 20 new:
  `NotificationDeliveryProcessorTests` covering success/transient-failure/retry/max-attempts/
  missing-email/business-isolation/no-duplicate-send/concurrent-claiming, `EmailTemplatesTests`
  covering subject selection, rendering, HTML-encoding, and no-sensitive-content). Live-backend
  acceptance: two new tests in `notification.delivery.live.test.ts` (Scenarios A-C + G-H: real
  TaskAssigned/ApprovalRequired/ProcessCompleted emails reaching `Sent`, cross-user security,
  duplicate-action safety; Scenarios D-F: a real provider failure that doesn't affect the business
  transaction, a real retry that eventually succeeds, and a real exhausted-retries run that ends
  `Failed`) plus all 10 pre-existing live test files re-run and still passing (17/17 total, up
  from 15/15).

### Added — Phase 6.1: Notification Foundation (In-App)
The first real capability of Phase 6, built on the existing `BPM.Notification` project (previously
an empty scaffold) plus a new `Notification` entity/table — In-App only, no Email/SMS/SLA/
escalation, per this phase's own scope boundary. **Phase 6 overall is NOT complete** — only 6.1.
- A `Notification` entity (`RecipientUserId`, `Type`, `Title`, `Message`, `RelatedEntityType`/
  `RelatedEntityId`, `IsRead`, `CreatedAt`, `ReadAt`) persisted via a new EF Core migration
  (`AddNotifications`), indexed on `RecipientUserId`, `(RecipientUserId, IsRead)`, and `CreatedAt`
  — the three access patterns the query service actually uses, nothing speculative.
- 8 notification types (`TaskAssigned`, `ApprovalRequired`, `ApprovalReturned`, `TaskCompleted`,
  `ApprovalCompleted`, `ProcessCompleted`, `ProcessRejected`, `ProcessReturned`) wired directly
  into the real Workflow/Approval engine — not a second event bus, not a controller-level `if`.
  `ApprovalReturned` and `ProcessReturned` are deliberately distinct (not overlapping): the former
  notifies other pending co-approvers whose assignment was just cancelled by a Return, the latter
  notifies the process Initiator that their request needs revision — two genuinely different
  audiences for the same backend event.
- Recipient resolution reuses the existing engine's own resolution exactly — a role-assigned
  UserTask notifies every active role holder (the same set `AssignmentResolver`'s Role branch
  computes); a Sequential ApprovalTask only notifies the first (currently-actionable) candidate,
  not the whole future queue; every process-level notification (Completed/Rejected/Returned)
  targets the real `ProcessInstance.InitiatorId`. No new assignment/recipient logic was invented.
- Notification creation is transactionally atomic with the business operation that produces it —
  for free, via the same "one `SaveChangesAsync` per engine operation" pattern this codebase
  already uses for `AuditLog` entries (`WorkflowTransitions.NewNotification`, called the same way
  `NewAuditLog` already is). If the engine method throws before its single `SaveChangesAsync`, no
  Notification is persisted either — proven by a duplicate-completion test where the second
  (rejected) attempt produces zero extra notifications.
- `GET /api/notifications`, `GET /api/notifications/unread-count`, `POST /api/notifications/{id}/read`,
  `POST /api/notifications/read-all` — every one scoped to the authenticated caller via
  `ICurrentUserService`, with no `userId` route segment or query parameter anywhere on the
  controller. Marking another user's notification read is rejected (`403
  NOTIFICATION_NOT_AUTHORIZED`) — including for an Administrator, since a notification is
  inherently private to its recipient and no existing capability grants Administrator override
  here (deliberately not added). Mark-read is idempotent (a second call is a harmless no-op, never
  re-stamps `ReadAt`).
- Frontend: a header `NotificationBell` (unread badge, popover list, mark-read-on-click with
  navigation to the related Task where one exists, mark-all-as-read) — no new page, no Dashboard,
  reusing the existing AntD/React Query/routing/API-client conventions throughout.
- Testing: 277 frontend tests total (up from 268) — 9 new (`NotificationBell.test.tsx`). 251
  backend tests (232 unchanged + 19 new: 10 `NotificationServiceTests` covering pagination/unread-
  filter/mark-read-idempotency/cross-user-IDOR, 9 `NotificationIntegrationTests` covering every
  trigger point against the real engine). Live-backend acceptance: a new
  `notification.live.test.ts` covering Scenarios A-H (task assignment, approval required, approval
  action, process completion, mark read, mark all read, cross-user security including query-
  parameter and route-ID manipulation, and duplicate-action safety) plus all 9 pre-existing live
  test files re-run and still passing (15/15 total, up from 14/14).

**Phase 6.1 = COMPLETE. Phase 6 as a whole is NOT complete** — 6.2 (Email Delivery), 6.3 (SLA
Foundation), and 6.4 (SLA Scheduler/Escalation) are unstarted, per this phase's own explicit stop
condition.

### Hardening — Phase 5.5.3/5.5.4: Operational Hardening & Final Phase 5 E2E (Phase 5.5 COMPLETE, Phase 5 COMPLETE)
An evidence-based security/operational audit across the whole Phase 5 surface (error contract,
loading/empty/error UX, pagination, concurrency, authorization/IDOR, Administration security,
audit coverage, mutation safety, React Query invalidation, routing/auth UX, Docker environment) —
not a rewrite. Most areas were already correct, verified rather than changed (existing
`ErrorHandlingMiddleware` error contract, existing `QueryStateView`/`ApiErrorAlert` UX pattern used
consistently, existing RowVersion concurrency on Process/Form Version/User/Department all already
tested, existing CORS/SPA-fallback/Docker health all correct). One genuine, real security gap was
found and fixed:
- **`GET /api/process-instances` and `GET /api/process-instances/{id}` had no caller-identity
  authorization at all** — any authenticated user could view any process instance, or list every
  instance in the system, regardless of whether they had any relationship to it. The same class of
  bug Phase 5.4.3 found and fixed for `ITaskQueryService.GetByIdAsync`, just never applied to this
  sibling service (there is no frontend UI for this endpoint yet — Process Instances remains a
  placeholder page — so the gap was latent, not exploited, but real). Fixed with the identical
  visibility rule: Administrator, the process's own Initiator, or a task/approval participant on
  that instance; everyone else gets `403 PROCESS_INSTANCE_NOT_AUTHORIZED`. 5 new backend tests
  (`ProcessInstanceQueryServiceTests.cs`).
- A new `phase5Final.live.test.ts` composes Administration (real Department/Role/User setup) with
  a Designer-serialized Process and Form, a full Applicant → Manager → Finance approval chain,
  worklist correctness, and Audit Log verification, in one real HTTP flow with four distinct
  logins — plus a cross-user security sweep (unauthorized access to another user's process
  instance/task/form-instance, wrong-assignee approval attempts, duplicate-approve rejection) that
  is exactly what caught the `ProcessInstanceQueryService` gap above.
- Testing: 232 backend tests total (up from 227) — 5 new. 268 frontend tests unchanged (the fix
  and its test are backend/live-only; nothing in the jsdom suite calls this service). Live-backend
  acceptance: 14/14 tests across 9 files (13/13 pre-existing + 1 new comprehensive final E2E), all
  passing against the rebuilt Docker stack.
- Docker: `bpm-api` rebuilt with the fix; all five containers (postgres, redis, minio, bpm-api,
  bpm-web) verified healthy; CORS, SPA fallback (`try_files ... /index.html`), and API/DB/MinIO/
  Redis connectivity all spot-checked and correct, no changes needed.
- Browser verification: unavailable (extension not connected), same as every prior phase —
  documented honestly, not faked.

**Phase 5.5 is now COMPLETE. Phase 5 as a whole is now COMPLETE.** Phase 6 (Notification + SLA),
Phase 7 (Dashboard + Reporting), and Phase 8 (Enterprise / Production Hardening) remain entirely
unstarted — see `PROGRESS.md`'s Phase 5.5.3/5.5.4 section for the full audit findings and evidence.

### Added — Phase 5.5.2: Administration Foundation & Audit Workspace
- A real Administration workspace (replacing the earlier placeholder) with Users, Departments,
  Roles, and Audit Logs sub-pages under `/administration/*`, reusing the existing Phase 1
  User/Organization/Department/Role/AuditLog backend — no second RBAC, identity, or audit system
  was built.
- Users: list/search/filter (department, status)/paginate (client-side over the existing
  `GET /api/users`), create, edit (display name/email/department/active toggle), with a
  disable-user confirmation dialog.
- Departments: list/search/paginate, create, edit (name/parent/manager) with hierarchy shown via
  a resolved Parent column, and a change-manager confirmation dialog. `Update` (previously
  missing entirely — Departments only had Create) is new; so is server-side validation of
  duplicate names, self-parent, circular-parent hierarchies, and — a genuine gap found during
  inspection — a nonexistent or inactive `ManagerUserId`, which was previously accepted silently.
- Roles: list with live member counts, create, and a checkbox-style "Manage Members" dialog
  backed by two new endpoints, `GET /api/roles/{id}/members` and `POST /api/roles/unassign`
  (Assign already existed; there was previously no way to see or remove membership at all).
  Removing someone else's Administrator role is allowed and audited; an Administrator cannot
  remove their *own* Administrator role (a narrow self-lockout guard — privilege *escalation* was
  already impossible, since `RolesController` was already fully Administrator-only).
- Audit Logs: a strictly read-only, server-side paginated and filterable (actor/entity
  type/entity id/date range) table. `AuditLogQueryService` previously accepted `Page`/`PageSize`
  but never returned `TotalCount`; it now returns a proper `PagedResult<AuditLogDto>`.
- Optimistic concurrency (RowVersion/ExpectedVersion) added to Users and Departments — both
  already had an unused `RowVersion` column since Phase 1, the same "column existed, nothing
  wired it up" pattern found for `ProcessVersion` (5.3.2) and `FormVersion` (5.4.1). A stale save
  is rejected with `409 USER_CONCURRENCY_CONFLICT` / `409 DEPARTMENT_CONCURRENCY_CONFLICT` and the
  UI shows "Data was modified by another administrator." with a Reload action, never a silent
  overwrite.
- Authorization is entirely backend-authoritative: every mutating endpoint (and every
  `RolesController`/`AuditLogsController` action, including reads) is independently
  `[Authorize(Roles = "Administrator")]`; the frontend additionally hides the nav item and gates
  the route for non-admins, but that is UX only. Verified live, over real HTTP with distinct
  Administrator/Normal-User/Unauthorized-User logins: normal users get `403` on every
  Administration endpoint regardless of URL id or query-param manipulation (`userId`, `entityId`,
  `roleId`), self-assigning Administrator is rejected, and modifying the Administrator account is
  rejected.
- Testing: 268 frontend tests total (up from 230) — 38 new (Users/Departments/Roles/Audit Logs
  page tests plus Administration navigation/route-guard tests). 227 backend tests (191 unchanged
  + 36 new: User/Department concurrency+validation, Department manager validation, Role
  membership/self-lockout, Audit Log pagination/filtering). Live-backend acceptance: one
  comprehensive scenario (Users/Departments/Roles create-assign-persist, Audit Log content,
  non-admin denial, IDOR via id/query-param manipulation, privilege-escalation rejection, and a
  real two-writer concurrency conflict with no silent overwrite) plus all 7 pre-existing live
  test files re-run and still passing.

### Added — Phase 5.5.1: Approvals Worklist & Operational Approval Center
- A dedicated Approvals workspace (replacing the earlier placeholder page) with Pending/Returned/
  Completed/All tabs, search, and server-side pagination — an operational view over exactly the
  approval work the current user participates in, backed by a new `GET /api/tasks/approvals`
  query endpoint. The Phase 3 Approval Engine itself is untouched: opening an item navigates to
  the existing Task Detail page, and every action (Approve/Reject/Return/Delegate/Transfer/Add
  Approver) still runs through the same backend endpoints Phase 5.4.4 already wired up.
- Approval progress ("AnyOne 1/3", etc.) reuses the exact same projection My Tasks already
  displays — one shared calculation, not a second one.
- Filters persist in the URL (`?status=&search=&page=`) so refresh, bookmarks, and browser
  back/forward all preserve the current view.
- After any approval action, every cached worklist view is invalidated — a just-approved item
  never keeps showing under its old status.
- Authorization is entirely server-derived (no client-suppliable user id anywhere in the new
  query); verified with cross-user tests, both at the integration level and live over real HTTP
  with four genuinely distinct logged-in users, that an unrelated user's worklist is empty and
  every task/form/action endpoint rejects them with `403`.
- Testing: 230 frontend tests total (up from 220) — 10 new Approvals page tests. 191 backend
  tests (184 unchanged + 7 new query tests covering authorization scoping, status/search
  filtering, pagination, and approval-progress correctness — no backend engine changes, only a
  new read-only query). Live-backend acceptance: a single comprehensive scenario covering
  no-cross-user-leakage, IDOR rejection across every task/form/action endpoint, duplicate-click
  protection (two concurrent Approve calls, exactly one succeeds), and a full Manager → Finance
  approval chain reflected correctly in each user's worklist — plus all 11 pre-existing live tests
  re-run and still passing.

### Added — Phase 5.4.4: Process + Form E2E Integration & Hardening
- Task Detail now offers the full approval action set — Approve, Reject, Return, Delegate,
  Transfer, and (Administrator-only) Add Approver — all backed by the existing Phase 3 Approval
  Engine endpoints; previously only Approve/Reject had UI. Delegate/Transfer/Add Approver use a
  real user picker (the same identity source the Process Designer and Form Runtime already use),
  not free text.
- My Tasks gained an Approval column showing an approval task's live policy and
  approved/required progress at a glance.
- Integration-hardened and proven end-to-end (not redesigned): a Form bound to a UserTask now has
  test coverage flowing through a full multi-step approval chain (Applicant → Manager → Finance →
  Completed) for the first time — conditional-required rules and calculated fields verified
  through a real sequential approval, not just a single-task scenario.
- Security hardening verified with real cross-user tests, not just re-read code: an unrelated user
  cannot view or act on another user's task, cannot view/save/submit another user's FormInstance,
  and cannot approve/delegate/transfer someone else's approval assignment — every case checked
  against a real Postgres-backed rejection, and — for the headline live scenario — against four
  genuinely distinct logged-in users over real HTTP, not four requests replaying one admin token.
- Confirmed and proved (rather than only documented) that a running ProcessInstance/FormInstance
  keeps the exact FormVersion it was created with even after a newer version of the same form is
  published, and that a duplicate/retried form submission is safely rejected rather than creating
  a second downstream approval task.
- Testing: 220 frontend tests total (up from 212) — 7 new Task Detail approval-action cases, 1 new
  My Tasks approval-column case. 184 backend tests (179 unchanged + 5 new integration tests
  covering the Process+Form+Approval chain, IDOR across task/form endpoints, FormVersion
  integrity, and duplicate-submission safety — no backend production code needed to change this
  phase; inspection found no gap requiring one). Live-backend acceptance: a single comprehensive
  scenario — build a form and process via the Designer's own functions, run it through four real
  users end to end (Applicant fills/saves/resumes/submits with a conditional rule and a calculated
  field, a spoofed value is never trusted, a stranger is rejected at every turn, Manager and
  Finance approve in sequence, the process completes, and the completed form rejects further
  edits) — plus all 10 pre-existing live tests re-run and still passing.

### Added — Phase 5.4.3: Form Runtime UX
- A real end-user Form Runtime: My Tasks → open a task → its bound Form loads and renders from the
  existing `FormSchema`, live business rules reacting exactly the way the Designer's own Preview
  already did (reusing the same `evaluateForm` implementation, not a second rule engine) → Save
  Draft / resume later / Submit → the task completes through the real Workflow Engine, not a mock.
- `TasksPage` (previously a placeholder) is now a real task list; `TaskDetailPage` (new,
  `/tasks/:id`) renders a form-bound UserTask's Form Runtime, a plain UserTask's direct Complete
  action, or minimal Approve/Reject for an ApprovalTask reached through the unified list.
- All 12 existing field types render, schema-driven — Text/Textarea/Number/Currency/Date/
  DateTime/Select/Radio/Checkbox reuse the existing Designer field-preview control in a new
  controlled mode; User and Department reuse the existing identity/organization pickers
  (`GET /api/users`/`/api/departments`) the Process Designer already uses; File reuses the
  existing attachment upload/list/download/delete endpoints — no raw bytes in FormData JSON, no
  second storage mechanism.
- Client-side validation (required — including rule-derived conditional required — type/format
  checks, existing `FormFieldValidation` constraints) shows inline per-field errors before
  Submit; a rejected backend submission maps its `{code, message}` errors back onto the
  responsible field using the same quote-extraction heuristic the Designer's own validation panel
  already established, rather than a generic toast. The backend remains fully authoritative
  regardless of what client validation decided.
- Save Draft / Resume / Submit reuse the existing `FormInstance`/`FormData` persistence and
  optimistic-concurrency model unchanged — a stale save gets the same `409
  FORM_CONCURRENCY_CONFLICT` banner with a Reload Latest action every other versioned-resource
  editor in this codebase already uses. Dirty-state tracking and a Stay/Leave navigation guard
  protect unsaved edits.
- **Backend fix**: `TaskQueryService.GetByIdAsync` had no caller-identity check at all — a
  pre-existing authorization gap (any authenticated user could load any task's full detail) found
  during this phase's own mandatory inspection, latent only because Task Detail didn't exist as
  real UI before now. Fixed to match `GetMyTasksAsync`'s own visibility rule (assignee, role
  assignee, approval participant, or Administrator); anyone else gets `403 TASK_NOT_AUTHORIZED`.
- Testing: 212 frontend tests total (up from 167) — new `runtimeValidation.test.ts` (10),
  `FormRuntime.test.tsx` (22), `TasksPage.test.tsx` (5), `TaskDetailPage.test.tsx` (8). 179 backend
  tests (175 unchanged + 4 new covering the authorization fix). Live-backend acceptance: a full
  Task → Form → conditional-required rejection → fill in → Submit → real Task/ProcessInstance
  completion scenario, run against the actual Workflow Engine, plus all 9 pre-existing live tests
  re-run and still passing.

### Added — Phase 5.4.2: Advanced Form Rules
- Business-rule-aware Form Designer: conditional Visibility/Enabled/Required rules, AND/OR/NOT
  compound conditions, and safe declarative Calculated fields (numeric literals, field references,
  `+ - * /`, parentheses only — no `eval()`, no dynamic compilation, no arbitrary code execution).
  Rules live inside the same `FormSchema` JSON `FormSchemaValidator`/`FormEngine` already own — no
  second schema, no new database table, no new API endpoints.
- Rule Builder integrated directly into the existing Properties Panel (no separate page): a "Rules"
  section per field with a type-aware condition editor (operators restricted to what's valid for
  the selected source field's type) and a formula editor for Calculated fields, with inline
  invalid-formula feedback.
- Backend `FormRuleValidator` rejects at publish time: unknown/missing referenced fields,
  self-dependency, an operator invalid for the source field's type, an incompatible comparison
  value, a malformed NOT/empty AND-OR group, and — via a dependency-graph cycle detector —
  circular rule dependencies (`A → B → A`).
- **Authoritative backend enforcement, not just Designer-time validation**: at submission, the
  backend recomputes every Calculated field's value itself (a client-submitted value for one is
  never trusted, not even compared) and derives the *effective* required-field set from matching
  rules — a client cannot bypass a conditionally-required field by omitting it or by it having
  appeared hidden in the last UI state it saw. A formula that can't be safely evaluated (division
  by zero) fails the request closed with a clear error rather than persisting a wrong number.
- Declarative default values (`FormFieldDefinition.DefaultValue`, accepted since Phase 4 but never
  actually applied until now) are applied once at `FormInstance` creation and never overwrite a
  later explicit user edit.
- Designer Preview now actually evaluates rules live — a hidden field doesn't render, a
  disabled-by-rule field can't be edited, a Calculated field shows its live-computed value — using
  the same evaluation logic a future runtime form renderer would use, so Preview and Runtime can
  never silently drift apart. This evaluation is UX-only; the backend always re-evaluates
  everything itself regardless of what the frontend believes.
- Undo/Redo now covers rule changes (Add/Edit/Delete Rule) exactly like field changes, via one
  combined history rather than two independently-stepping ones. Deleting a field also removes any
  rule that targeted or referenced it, rather than leaving a dangling reference behind.
- An unrecognized rule `type` (a future phase's addition) is preserved read-only end to end —
  Designer and backend alike — never silently dropped or corrupting the rest of the schema on
  save, the same "preserve, don't destroy" treatment an unsupported field type already got.
- Testing: 167 frontend tests total (up from 134) — new `ruleEngine.test.ts` (15),
  `RuleBuilder.test.tsx` (14), 4 new `FormDesigner.test.tsx` cases. 175 backend tests (118
  unchanged + 57 new: formula parser, rule validator, rule evaluation engine, and 6 new live-
  database integration tests proving the conditional-required bypass is genuinely blocked and a
  spoofed calculated value is genuinely ignored). Live-backend acceptance: 2/2 new tests passing —
  Designer-built rules survive Save Draft → Reload → Validate → Publish → read-only, then a real
  `FormInstance` submission is genuinely rejected when a conditionally-required field is missing
  and genuinely accepted once it's filled in (or once the condition no longer applies) — plus all
  7 pre-existing live tests (Process Management, Process Designer, Form Designer foundation)
  re-run and still passing.

### Added — Phase 5.4.1: Form Designer Foundation
- Visual Form Designer over the existing Form Engine: Field Palette (the same 12 field types
  `FormSchemaValidator` already supports) / Form Canvas / Properties Panel, wired into the
  existing Form Version lifecycle (Draft → Save/Validate → Publish → read-only). No second,
  frontend-only schema — a dedicated `formSchemaModel.ts` serializer/deserializer layer converts
  to/from the exact same `FormSchema` JSON the backend validates and stores.
- Field keys are user-editable (validated non-empty/unique/safe-identifier format) and stay
  stable across reordering — the Designer's internal React list identity (`internalId`) is kept
  deliberately separate from the field's semantic `key`, so renaming a key mid-edit never
  remounts the row or loses selection.
- An unsupported/reserved field type (RichText, Table, Signature, Formula, Computed) is preserved
  read-only and round-tripped verbatim, never silently discarded or reinterpreted.
- Select/Radio fields get a proper Options editor (stable `value` + editable `label`, never
  label-as-identifier), seeded with one starter option so a new one is never published empty.
- A dedicated Validation Panel maps each backend validation error to the affected field where
  possible (clicking selects that field) — backend validation (via the existing
  `FormSchemaValidator`, now exposed at `POST /api/form-definitions/validate`) remains fully
  authoritative; this only ever renders what it already decided.
- A Preview mode renders the form as a user would see it (all 12 supported field types,
  interactive but with no real submission/workflow execution/persistence), toggled without
  leaving the Designer.
- Bounded (50-entry) undo/redo for field add/delete/reorder/duplicate/property/option changes.
- New Form Management pages (`/forms`, `/forms/:id`, `/forms/:id/versions/:versionId`) — Form
  List → Form Detail → Draft Version → Open Form Designer → Edit → Save Draft → Validate →
  Publish is the only path; no second form/version management system. Mirrors the equivalent
  Process Management pages file-for-file.
- **Backend**: `FormVersion` gained a Save-Draft endpoint (`PUT
  /api/form-definitions/{id}/versions/{versionId}`) built with the identical RowVersion/
  ExpectedVersion optimistic-concurrency pattern Phase 5.3.2 added for `ProcessVersion` — a stale
  save fails closed with `409 FORM_VERSION_CONCURRENCY_CONFLICT`, and the Designer shows the same
  dedicated conflict banner with a "Reload" recovery action Phase 5.3.2 built, never a silent
  overwrite. A non-Draft version rejects any edit outright with `409 VERSION_NOT_DRAFT`, published
  immutability enforced server-side. New `PUT /api/form-definitions/{id}` (metadata) endpoint
  too.
- Testing: 134 frontend tests total (up from 75) — new `formSchemaModel.test.ts` (9, the
  round-trip semantic-equivalence suite), `FormDesigner.test.tsx` (13), `PropertiesPanel.test.tsx`
  (13), `FormDefinitionsPage.test.tsx` (7), `FormDetailPage.test.tsx` (4),
  `FormVersionEditorPage.test.tsx` (13, including the same three-scenario 409-conflict coverage
  the Process side has). 118 backend tests (106 unchanged + 12 new covering the new Save-Draft/
  concurrency/validate endpoints). Live-backend acceptance: 2/2 new tests passing, building a
  schema with the Designer's own serialization functions (Text/Number/Currency/Select/Department/
  File fields) and walking it through Save → Reload → Validate → Publish → verified read-only
  against the real engine, plus confirming the published form's Process-Designer binding contract
  still works end to end; the 5 pre-existing Process live tests were re-run and still pass (no
  regression).

### Added — Phase 5.3.2: Designer Workflow Integration & Hardening
- Approval assignment editor now sources every option from real backend APIs
  (`GET /api/roles`/`/api/users`/`/api/departments`) instead of free text — Role/User/Department/
  DepartmentManager/ProcessInitiator all match `AssignmentResolver`'s actual value semantics
  exactly. A previously-saved value that no longer matches any current option is kept visible and
  selected (labeled "not found") rather than silently discarded.
- UserTask's Form reference is now a picker over real, *Published* `FormDefinition`s
  (`GET /api/form-definitions`) rather than a free-text key. ApprovalTask deliberately has no Form
  field: traced `WorkflowTransitions.CreateTaskForNodeAsync` and confirmed it only reads
  `node.Form` for UserTask — an ApprovalTask's Form would be silently ignored by the engine, so
  the Designer never offers it there (see PROGRESS.md's Phase 5.3.2 section for the full trace).
- New dedicated Validation Panel (replacing the old inline alert): lists each backend validation
  error, maps it to the affected node where possible (clicking selects that node), and shows a
  clear "Workflow definition is valid" success state. Backend validation remains fully
  authoritative — this only renders what it already decided.
- **Backend fix**: `UpdateVersionAsync` (Save Draft) had no optimistic-concurrency check at all —
  a real pre-existing gap found during this phase's own mandatory read-through, not a new feature.
  Two people editing the same Draft concurrently could silently overwrite each other. Fixed with
  the same RowVersion/ExpectedVersion pattern `FormEngine.SaveDataAsync` already used elsewhere:
  `ProcessVersionDto` gained `rowVersion`, `UpdateProcessVersionRequest` gained a required
  `expectedVersion`, and a stale save now fails closed with `409 PROCESS_VERSION_CONCURRENCY_CONFLICT`.
  The Designer shows a dedicated conflict banner with a "Reload" recovery action — never a silent
  overwrite and never a silent retry.
- Keyboard shortcuts (Escape to deselect, Delete/Backspace to remove the selected node, guarded
  against firing while a text field is focused) and an explicit Fit View toolbar button.
- Testing: 75 frontend tests total (up from 53) — new `PropertiesPanel.test.tsx` (17 tests
  covering every assignment type/policy/action/form-binding case), 3 new concurrency tests, 2 new
  validation-error-node-mapping tests. 106 backend tests (103 unchanged + 3 new covering the
  concurrency fix). Live-backend acceptance: 5/5 passing, including the full scenario from
  Create Process through a real running ProcessInstance with a real FormInstance created for it,
  and a genuine two-writer 409 conflict against the live database.

### Added — Phase 5.3.1: Visual Process Designer Foundation
- `@xyflow/react` (React Flow) canvas replaces the JSON editor as the *default* way to edit a
  Draft ProcessVersion; the JSON editor is kept as an "Advanced (JSON)" mode (switchable without
  losing edits) per this iteration's own instruction not to remove it until the visual designer
  was proven. No backend code changed — the designer adapts entirely to the existing
  `WorkflowDefinition` JSON contract.
- Node palette (Start/UserTask/ApprovalTask/End only — matching what the engine can execute
  today), drag-or-click node creation, connect/select/delete, a Properties Panel that only ever
  shows backend-supported fields per node type, bounded (50-entry) undo/redo, and a dedicated
  `designer/graphModel.ts` serializer/deserializer layer (`deserializeWorkflowDefinition`/
  `serializeWorkflowDefinition`) — no JSON transformation logic scattered across components.
- An existing definition's unsupported node types (gateways, Timer, ServiceTask, SubProcess, ...)
  are preserved read-only and round-tripped verbatim, never silently discarded.
- Architecture note: the backend has no node-position field, so the designer auto-layouts on
  every load rather than persisting manual arrangement — a documented tradeoff, not a backend
  schema change (see PROGRESS.md's Phase 5.3.1 section for the alternatives considered).
- A real bug was found and fixed before merge: the designer's dirty-tracking baseline chased the
  live graph instead of staying fixed at load, making "unsaved changes" always read false after
  the first edit. Fixed by computing the baseline once at mount instead of reactively.
- Testing: 53 frontend tests total (up from 18), including a round-trip semantic-equivalence
  suite for the serializer (order-independent comparison, per this iteration's explicit
  instruction not to rely on JSON string/array-order equality) and a live-backend acceptance test
  that builds a graph the same way the UI does and runs it through validate → publish → an actual
  running process instance. Backend: 103/103 unchanged (no backend files touched this iteration).

### Added — Phase 5.2: Process Management UI
- Real Process List (`ProcessDefinitionsPage`) against the backend API: search, status filter,
  server-side pagination, refresh, and a Create Process modal (client + backend validation,
  including the duplicate-key `409`). A created process always starts `Draft`.
- Process Detail page: metadata + an Edit modal (name/description/category — Key is immutable),
  Version History table (actor ids resolved to display names via `GET /api/users`), and
  Create Version / Publish actions whose enabled state mirrors backend rules exactly (never a
  button that looks clickable but would 409).
- A basic, intentionally temporary JSON Definition editor (`ProcessVersionEditorPage`) for a Draft
  version: loads/edits the real `WorkflowDefinition` JSON (no second frontend-only schema), a
  client-side JSON-syntax check, `[Validate]` (calls the backend's authoritative validator without
  publishing), `[Save Draft]`, an unsaved-changes indicator with a `beforeunload` guard and an
  in-app discard-confirm, and read-only rendering once a version is Published.
- Backend gaps found and closed (see PROGRESS.md's Phase 5.2 section for the full reasoning):
  `CreatedBy`/`UpdatedAt` were never stamped on `ProcessDefinition`/`ProcessVersion`; there was no
  way to update a Draft version's `DefinitionJson` in place (`PUT .../versions/{versionId}`, now
  added) or a process definition's own metadata (`PUT /api/process-definitions/{id}`, now added);
  there was no standalone validate-without-publish endpoint (`POST /api/process-definitions/validate`,
  now added, reusing the existing pure `WorkflowDefinitionValidator`); and the list endpoint had no
  search/filter/pagination (`ProcessDefinitionQuery` + a new `PagedResult<T>` envelope, now added).
- Vitest + React Testing Library set up from scratch (no frontend test framework existed before);
  18 component tests plus a live-backend acceptance test (`process.live.test.ts`,
  `npm run test:live`) that runs the full Login → Create Process → Create Draft Version → Edit →
  Validate → Save → Publish → Verify flow against the real running API, not a mock, along with an
  authorization scenario (non-admin gets `403` on create/validate, `200` on read; unauthenticated
  gets `401`).
- 10 new backend tests (`ProcessDefinitionServiceTests`) — 103/103 backend tests passing, zero
  regressions.
- Phase 5.3 (the visual Process Designer canvas) is explicitly **not** started — this editor is a
  deliberate placeholder for it.

### Added — Phase 5.1: Frontend Foundation (scaffold + auth + shell)
- `frontend/` scaffolded: React 19 + TypeScript + Vite, Ant Design v6, React Router v7, TanStack
  Query v5, Zustand v5, Axios, matching CLAUDE.md's declared stack and folder layout
  (`src/{components,pages,services,hooks,stores,types}/`, `src/features/{process,workflow,form,
  task,approval,administration}/`).
- Login flow against the existing `/api/auth/login`: session held in a Zustand store persisted to
  `localStorage`, Axios instance attaches the bearer token and clears the session on `401`.
- `ProtectedRoute` + `AppLayout` (AntD Sider/Menu) provide route guarding and navigation across
  the six feature areas; each feature currently renders a placeholder page only.
- Backend: added a CORS policy (`Program.cs`) so a browser-based frontend can call the API at
  all — previously there was no CORS policy, which silently blocked every origin.
- `frontend/Dockerfile` + `nginx.conf` + `docker-entrypoint.sh` implement the `bpm-web`
  docker-compose service that's had a placeholder since Phase 1.
- Explicitly deferred: the visual Process/Form Designer canvas (React Flow) and all real CRUD UI
  — this iteration is scaffold + auth + shell only, by explicit user scoping.

### Added — Phase 4: Form Engine
- Domain: `FormDefinition`, `FormVersion` (immutable once published), `FormInstance`
  (Draft/Submitted/Locked/Cancelled), `FormData` (values kept separate from schema), `Attachment`
  (metadata only — bytes live in MinIO).
- `FormSchemaValidator` and `FormDataValidator` (`BPM.Workflow`): fail-closed, declarative-only
  (no expression engine) validation of form schemas and submitted values.
- `FormEngine` (`BPM.Workflow.Engine`): publish, create/save/submit/cancel a `FormInstance`.
  Submitting a form bound to a UserTask completes that task and advances the workflow via the
  same shared transition code `ApprovalEngine` uses — `WorkflowEngine` has no form-specific code.
- Workflow integration: `WorkflowNodeDefinition.Form` lets a UserTask reference a form
  definition; the engine auto-creates a pinned `FormInstance` when it creates that task.
  `UserTask` assignment gained `ProcessInitiator` support (previously User/Role only) for the
  "applicant fills their own form" pattern.
- `IFormAuthorizationService`: shared identity check (creator / task assignee / process
  approvers / initiator) used by both reads and writes so the two surfaces can't drift.
- Real MinIO-backed attachment upload/download/list/delete, with size limits, path-traversal
  protection, and content-type/extension cross-checking (never trusts the client's declared MIME
  type).
- New API: `/api/form-definitions` (+ `/versions`, `/publish`), `/api/form-instances` (+ `/data`,
  `/submit`, `/cancel`, `/attachments`), `/api/attachments/{id}`.
- 36 new tests (22 unit, 14 integration against live Postgres — including a full
  workflow+form+approval integration test using the real engines together) — 93/93 total, zero
  regressions in the 57 from Phase 2/3.
- Verified live against Docker: the complete Skill.md §37 acceptance scenario end to end, plus
  real file upload/download round-tripped through MinIO.

### Changed — Refactor pass
- Each layer (`BPM.Infrastructure`, `BPM.Identity`, `BPM.Workflow`) now owns its own DI
  registration via an `AddXxx()` extension method instead of `Program.cs` wiring ~15 concrete
  services directly; `Program.cs` no longer references concrete types from those layers.
- Added `ICurrentUserService.RequireUserId()`, removing 10 repeated unsafe `UserId!.Value` casts.
- `CLAUDE.md`/`Skill.md`/`PROGRESS.md` are gitignored and were purged from git history before the
  first push — they're AI-assisted-dev context, kept locally only, not part of the public repo.

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
