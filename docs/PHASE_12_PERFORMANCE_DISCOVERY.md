# PHASE 12 — PERFORMANCE & SCALABILITY — ARCHITECTURE DISCOVERY REPORT

Read-only discovery. No production code, schema, migration, dependency, API contract, or behavior
was modified. No Redis/cache/queue/CQRS/microservice/event-bus was introduced. All findings below
are evidence-based — cited to a file/line, an `EXPLAIN` plan, or an actual measured request against
the real Docker stack, never a guess.

Five parallel read-only investigation passes covered the full brief: (1) database entity growth,
indexes, and capacity projection — including real `EXPLAIN` plans and index inspection against the
dev database's own accumulated data (not a synthetic/empty test DB); (2) EF Core query-layer
analysis across every query service; (3) API/pagination/frontend performance; (4) background
worker throughput, concurrency/locking, and memory/CPU; (5) Docker capacity, Redis, and a
non-destructive live benchmark against the running stack.

---

## 1. Executive Summary

The system is **not broadly performance-impaired**. Most of the codebase already follows sound,
previously-established patterns (bounded follow-up queries, `AsNoTracking`, real server-side
pagination where it matters). Discovery found a small, concrete set of real issues, the most
significant being: **one endpoint (`/api/analytics/overview`) measured ~100x slower than every
other endpoint tested**, one confirmed-but-scoped-out 2N+1 query (`BuildProcessComparisonAsync`),
a pagination integer-overflow bug (already fixed once elsewhere) still present in **four** other
paginated services, one endpoint with no pagination at all (`GetMyTasksAsync`), and a handful of
low-risk frontend/config gaps (no query-cache defaults, an unsplit 1.78MB JS bundle, no Docker
resource limits). **No architectural change is warranted.** The recommended Phase 12 scope is
narrow and query/config-level only.

---

## 2. Current Architecture

Unchanged from Phase 10/11: .NET 10 Modular Monolith, React 19 + TypeScript + Vite frontend,
PostgreSQL, MinIO, Redis (confirmed unused), Docker Compose (`bpm-api`, `bpm-web`, `postgres`,
`redis`, `minio`), 3 persistent named volumes. No message queue, no event bus, no CQRS, no
microservices anywhere in this repository.

---

## 3. Current Performance Baseline

**A real, non-destructive benchmark was run against the actual running Docker stack.** The dev
database is not empty/synthetic — it has accumulated real data across every live-test run in this
project's history: **2,139 Users, 1,145 ProcessDefinitions, 1,363 ProcessInstances, 1,976
TaskInstances, 23,815 AuditLogs, 3,005 Notifications, 293 TaskSlas**. This makes the benchmark
below meaningful evidence, though still short of a true 10x/100x production scale.

Single-request timings (local Docker Desktop, loopback — absolute numbers won't generalize to a
real network deployment, but relative differences between endpoints are informative):

| Endpoint | Status | Response size | Time |
|---|---|---|---|
| `GET /api/process-definitions` | 200 | 8,227 B | 0.006s |
| `GET /api/process-monitoring` | 200 | 13,175 B | 0.011s |
| `GET /api/dashboard` | 200 | 1,835 B | 0.011s |
| `GET /api/reports/summary` | 200 | 217,634 B | 0.020s |
| **`GET /api/analytics/overview`** | 200 | **675,822 B** | **1.125s** |
| `GET /api/notifications` | 200 | 6,342 B | 0.005s |
| `GET /api/sla-policies` | 200 | 49,297 B | 0.004s |
| `GET /api/users` | 200 | **586,990 B** | 0.012s |
| `GET /api/audit-logs?pageSize=50` | 200 | 16,815 B | 0.010s |
| `GET /api/tasks` | 200 | 118,051 B | 0.011s |

**`/api/analytics/overview` is ~100x slower than every other endpoint tested and returns the
largest payload**, at only ~1,145 process definitions / 1,363 instances — the single clearest,
most concrete performance signal in this entire discovery. `/api/users` returns a 587KB payload
unpaginated for 2,139 rows — still fast server-side (12ms, a simple flat query) but a real,
measured client-payload cost of the established "fetch everything" Administration pattern at this
scale. No write was performed beyond the login itself; the stack was left running, untouched.

---

## 4. Database Analysis

Every major entity (`ProcessInstance`, `TaskInstance`, `FormInstance`, `AuditLog`, `Notification`,
`NotificationDelivery`, `TaskSla`, `Attachment`, `ApprovalAssignment`, `User`/`Organization`/
`Department`/`Role`/`UserRole`, `ProcessDefinition`/`ProcessVersion`, `SlaPolicy`) is **insert-only/
append-only** — confirmed by re-grepping `[HttpDelete]` across every controller: only
`AttachmentsController` has any delete path.

**Real `EXPLAIN` evidence against the actual dev database** (23,815 `AuditLogs` rows, 1,976
`TaskInstances`, 1,363 `ProcessInstances`):
- `AuditLogs` paginated-by-`Timestamp` query → **Index Scan Backward using `IX_AuditLogs_Timestamp`** — confirmed index-backed on real data.
- `AuditLogs` count query → **Index Only Scan** — confirmed index-backed.
- `TaskInstances` filtered by `AssigneeId` → **Seq Scan**, despite an index existing on `AssigneeId` — at only 1,976 rows, PostgreSQL's own planner correctly judges a sequential scan cheaper at this small scale (expected, standard behavior, not a missing-index bug); very likely flips to an Index Scan once row counts grow 10x-100x, since a specific-user filter becomes more selective against a bigger table. **Honest limitation stated explicitly: this particular `EXPLAIN` result is inconclusive at current scale, not a confirmed defect.**

**New finding — `TaskInstance` has no index on `CreatedAt`**, the column `TaskQueryService
.GetMyTasksAsync` sorts by (see §5) — `AssigneeId`/`Status`/`DueAt` are indexed, `CreatedAt` is not.

**New finding — leading-wildcard `ILike` search has no supporting index anywhere.** 4 call sites
(`ProcessDefinitionService.cs`, `ProcessMonitoringQueryService.cs`, `TaskQueryService.cs`) all use
`EF.Functions.ILike(x, $"%{search}%")` with a **leading** `%`. PostgreSQL cannot use a standard
B-tree index for a leading-wildcard pattern; no `pg_trgm` extension or GIN trigram index exists
anywhere in this codebase (confirmed via migration/configuration grep). Currently harmless at
today's row counts; a real, growing cost as tables scale, since every search-filtered request
forces a sequential scan of the (already authorized+filtered) candidate set.

All other indexes are appropriately scoped to their actual query-access patterns — `TaskSla`'s
`(Status, DueAt)`/`(Status, WarningAt)` composite indexes exactly match the SLA scheduler's claim
query shape; `NotificationDelivery`'s `(Status, NextAttemptAt)`/`(Channel, Status)` match its claim
query; `FormInstance`'s filtered-unique `TaskInstanceId` index is correct for the nullable 1:1
relationship.

One residual note from Phase 11: `NotificationDelivery.NotificationId` has an index but **no
enforced FK constraint** — an integrity note, not a performance one, restated here only for
completeness.

---

## 5. EF Core Query Analysis

**Phase 10's `AnalyticsQueryService.BuildNodeAnalyticsAsync` fix confirmed still intact** — 3 total
queries (grouped node discovery, batched duration rows, batched overdue rows), the `foreach` loop
does only in-memory `Dictionary` lookups, zero DB calls inside it. Not regressed.

**CONFIRMED — `AnalyticsQueryService.BuildProcessComparisonAsync` still has an unfixed 2N+1**,
exactly as Phase 10's own discovery flagged and deliberately left out of that phase's scope. Its
`foreach (var item in grouped)` loop issues 2 queries per distinct `ProcessDefinitionId` in the
authorized+filtered scope. Severity: bounded by the number of *distinct process definitions* (a
realistically small, slow-growing number — tens to low hundreds for most orgs), smaller in
practice than the per-node count that made `BuildNodeAnalyticsAsync` worth fixing, but the same bug
shape and would scale linearly with process-definition catalog growth.

**No other N+1/2N+1 pattern found anywhere else** — every other multi-row resolution across
Dashboard, Process Monitoring, Process Instance Detail, Task Query, Audit Log Query, Notification,
and every Administration service uses the established "bounded follow-up query keyed by a batched
id list" pattern (`ToDictionaryAsync` after `.Where(x => ids.Contains(...))`), confirmed by direct
inspection of every loop in every file — all are pure in-memory iteration with zero `await`/DB
calls inside them.

No unnecessary `Include`, no Include-then-filter-in-memory, no client-side evaluation, no premature
materialization, and no duplicate/repeated query within one request was found anywhere (beyond the
standard, accepted EF pagination `Count` + `Skip/Take` double-scan, present everywhere paginated
and not a bug). `AsNoTracking` is used consistently everywhere it matters; the few `Dashboard`
aggregate queries that omit it are harmless since they terminate in `CountAsync`/`GroupBy().Count()`
and never materialize tracked entities regardless. `AsSplitQuery` is unused, but its Cartesian-
explosion risk is **not currently reachable** — no query anywhere includes 2+ sibling collection
navigations on one root.

**New finding — the pagination `int`-overflow bug Phase 7.2.3 fixed once (in
`ProcessMonitoringQueryService` only) is unfixed, by direct code comparison, in four more
services**: `AuditLogQueryService.cs:48`, `ProcessDefinitionService.cs:80`,
`TaskQueryService.cs:114` (`GetApprovalWorklistAsync`), and `NotificationService.cs:39` — all use
plain `(page - 1) * pageSize` arithmetic with no overflow guard, identical to the pre-fix code the
Phase 7.2.3 fix replaced. An extreme `Page` value overflows `int`, wraps to a negative `Skip()`
argument, and Postgres rejects it with a raw `PostgresException` that `ErrorHandlingMiddleware`
doesn't catch (it only maps `AppException`/`ValidationException`) — the exact failure mode Phase
7.2.3's own fix was written to prevent, now confirmed reachable through 4 other endpoints.

**New finding — `TaskQueryService.GetMyTasksAsync` has no pagination at all.** It fetches every
task the calling user has ever been assignee/role-eligible/approval-participant on, unbounded,
sorted by the unindexed `CreatedAt` column (§4). At realistic single-user scale today this is
small; for a long-tenured heavy user across years of real usage this grows unbounded with no limit.

---

## 6. API Analysis

Pagination `PageSize` maximums are enforced everywhere checked (200 for Audit/Notification/Task/
Process-Definition lists, 10,001 for Process Monitoring/Reporting — the Phase 7.3 CSV-export-reuse
value, confirmed still correct). All pagination is OFFSET-based; **no evidence anywhere in this
app's actual UI/API usage pattern of a real large-offset use case** — every paginated list is
narrow, filtered, recent-first, and realistically paged only a handful of times per session.
Keyset pagination is **not warranted by any evidence found**.

Dashboard's payload is small and bounded (`RecentActivity` capped at `Take(10)`). Reporting's CSV
export is memory-buffered (not streamed) but bounded by `ReportExportLimits.MaxExportRows = 10000`
(confirmed still in place), a ~2-5MB worst case — acceptable at that cap. Attachment upload buffers
the whole file in memory before hashing/uploading, bounded by the 25MB cap — acceptable for
realistic concurrent-upload volumes; the only path to a real concern is very high concurrent
upload volume with no per-request memory ceiling, which is a container-resource-limit question
(§11), not an application defect. Analytics' `AnalyticsQuery` has no maximum date-range length
enforced — a caller requesting a multi-year range at daily granularity could produce a
correspondingly larger trend array; bounded in practice by real row count in range, not bucket
count alone — a real but unconfirmed (no evidence anyone requests such a range) concern.

Zero blocking I/O found anywhere (`grep` for `.Result`/`.Wait()`/`.GetAwaiter().GetResult()`
returned zero matches across the whole backend) — the codebase is genuinely async end-to-end. No
explicit Kestrel/request-timeout configuration exists; ASP.NET Core defaults apply, unexamined, not
evidenced as a problem.

---

## 7. Frontend Analysis

**CONFIRMED — no global TanStack Query cache defaults.** `App.tsx` constructs `new QueryClient()`
with zero options, so `staleTime` defaults to `0` and `refetchOnWindowFocus` defaults to `true`
(TanStack Query v5 defaults) — every page revisit or window refocus triggers a fresh API call for
every query in the app, even for data fetched moments earlier. Only one query (`AnalyticsPage`'s
Departments dropdown) explicitly opts out with its own `staleTime`. This is a real, low-risk,
one-line fix (a `QueryClient` default-options change), not a redesign.

**CONFIRMED, directly measured from the actual `dist/` build artifact**: a single unsplit JS bundle,
1,823,752 bytes (~1.78MB), matching Phase 10's own build-warning figure. Zero `React.lazy`/
`Suspense` usage anywhere in the route tree; no manual chunking configured in `vite.config.ts`.
Every route (Dashboard, Designer, Analytics, Administration, etc.) ships in one bundle loaded on
first paint. Route-based code-splitting via `React.lazy` is the standard, already-in-toolkit fix —
no new dependency needed.

AntD `Table`'s client-side pagination is correctly applied on every "fetch everything" list page
(e.g. `UsersPage.tsx`'s `pagination={{ showSizeChanger: true, defaultPageSize: 20 }}`) — only the
current page's rows are rendered in the DOM, so the established fetch-everything-then-paginate-
client-side pattern remains reasonable for typical admin-table row counts, though §3's measured
587KB `/api/users` payload is the real cost that pattern now carries at 2,139 rows specifically.

No new state-management library or UI framework is recommended or needed — every finding here is
addressable with the existing React 19 + TanStack Query + Ant Design v6 stack.

---

## 8. Worker Analysis

Both `EmailDeliveryWorker` and `SlaSchedulerWorker` are in-process `BackgroundService`s (no
separate worker container). Both catch every exception per-tick and never crash the host.

- **EmailDeliveryWorker**: poll 15s, batch 20, stale-claim timeout 300s, backoff `30s × attempt`, max 5 attempts. Claim is a genuine single-statement atomic conditional `ExecuteUpdateAsync`. **Throughput ceiling: ~1.33 rows/sec ≈ 4,800/hour ≈ 115,200/day.**
- **SlaSchedulerWorker**: poll 60s, batch 50. Each candidate can attempt up to 3 independent claims (Overdue/Warning/Escalation), each in its own short, fast transaction (claim + a few SELECTs + `SaveChangesAsync` — no HTTP/file/MinIO/SMTP wait inside any transaction). **Throughput ceiling: ~0.83 rows/sec ≈ 3,000 transitions/hour ≈ 72,000/day.**

**Which bottlenecks first at 10x/100x/1000x scale, reasoned from the actual event graph (not
guessed)**: every SLA transition itself creates a `Notification`+`NotificationDelivery` via the
same shared factory workflow/approval events use — so **100% of SLA worker output becomes
additional input to the Email worker's own queue**, on top of every other notification-triggering
event in the system (task-assigned, approval-required/returned, process-returned/rejected/
completed, etc. — a strictly larger, unconditional event surface, since SLA policies are opt-in
per `(ProcessDefinitionId, NodeId)` while task/approval lifecycle notifications fire unconditionally
for every process). **`EmailDeliveryWorker` is the more likely bottleneck at scale** despite its
nominally higher per-tick ceiling — its input stream compounds (general workflow activity + 100%
of SLA output) while SLA's input stays limited to the policy-covered subset.

---

## 9. Concurrency Analysis

No `FOR UPDATE`/explicit row locking exists anywhere (confirmed by grep) — all concurrency is
optimistic (RowVersion/ExpectedVersion) or atomic conditional `ExecuteUpdateAsync` claiming.
NotificationDelivery and SLA claim mechanisms are both confirmed genuine single-statement atomic
operations, not SELECT-then-UPDATE races. Approval/Workflow transitions use one `SaveChangesAsync`
per action, no long-held transaction. Phase 10's Process-Start/Role-Assignment unique-violation
catches are terminal (`409`, no retry loop anywhere in the codebase) — a burst of concurrent
duplicate requests produces cheap, clean `409`s, never a retry storm (no retry mechanism exists to
storm with). Attachment operations hold no DB transaction open during the MinIO network call
(confirmed: `_storage.SaveAsync` happens before `_db.SaveChangesAsync`, no `BeginTransactionAsync`
anywhere in `AttachmentService.cs`). **No CONFIRMED locking/contention/deadlock issue found
anywhere.** The one real throughput-shaped concern (not a locking one) is each worker's per-row
serial query pattern inside a bounded batch (§8) — POTENTIAL at very high per-tick volume, not a
lock contention risk.

---

## 10. Memory / CPU Analysis

58 `ToListAsync`/`ToArrayAsync` call sites across 25 files, all categorized: Administration
services are the established, deliberate "fetch everything" pattern over small admin tables
(ACCEPTABLE); query services with real pagination/`Take` bounds stay bounded; `TaskQueryService
.GetMyTasksAsync` and single-instance-scoped detail queries are bounded by realistic per-user/
per-instance counts, not global table size. **No new unbounded fetch found beyond what was already
accepted design.**

**`WorkflowJson.TryDeserialize` confirmed called exactly once per workflow action** — the parsed
`WorkflowDefinition` graph is passed as a parameter down through every internal step
(`WorkflowTransitions.AdvanceFromAsync`/`CreateTaskForNodeAsync`), never re-parsed within one
action. This means Phase 10's `FormVersionId` addition (one ~36-character GUID string per
Form-referencing node) has **negligible** parse-cost impact — the parse already happens exactly
once regardless, and the added field is trivial relative to a whole workflow graph. Analytics'
in-memory `GroupBy` aggregation (the Phase 10 fix) operates over data fetched via a single bulk
query, scoped to the caller's own authorized+filtered set — memory use scales with a user's visible
data, not the whole table.

No single request was found that plausibly loads an unusually large amount of data into memory
relative to its own established bounds (25MB attachment cap, 10,000-row CSV cap, per-instance-
scoped detail queries).

---

## 11. Docker / Deployment Capacity

No `mem_limit`/`cpus`/`deploy.resources.limits` exists on any of the 5 services (confirmed,
re-verified from Phase 11's own finding). No `Maximum Pool Size` set on the Postgres connection
string — Npgsql's default (100) applies, unexamined. `docker stats --no-stream` idle/light-load
snapshot: `bpm-api` 0.69% CPU / 218MB, `minio` 0.09% / 81MB, `postgres` 0.00% / 127MB, `redis`
0.67% / 4.5MB, `bpm-web` 0.00% / 15MB — all near-zero at current (no active load) usage. **POTENTIAL,
not CONFIRMED**: since nothing caps any container, one under heavy real load could theoretically
starve host resources for the others — no evidence of actual contention exists today.

---

## 12. Redis Assessment

Confirmed, fresh grep pass: zero real code references to `StackExchange.Redis`/
`IConnectionMultiplexer`/`IDistributedCache` anywhere in `src/`; zero Redis package reference in
any `.csproj`. **Existing unused infrastructure — DEFER.** Not introduced for Phase 12.

---

## 13. 10x / 100x Capacity Projection

Reasoned from the actual index/query evidence in §4-5, not guessed. This repository has no real
10x/100x dataset — the only real-data evidence is the ~24k-row `AuditLogs`/~1.4k-row
`ProcessInstances` accumulated from this project's own testing history, used where genuinely
applicable below.

| Entity | Baseline (today) | 10x | 100x |
|---|---|---|---|
| AuditLog | ~24k rows, index-backed pagination confirmed via real `EXPLAIN` | Query cost stays roughly flat (index scan touches only the requested page); storage grows linearly (dominated by `OldValue`/`NewValue` JSON) | **ACCEPTABLE** — same reasoning holds; the unfixed `int`-overflow bug (§5) becomes more *reachable* as realistic large `Page` values become plausible, a correctness issue, not a scale-driven perf one |
| Notification/NotificationDelivery | Composite indexes match both read and worker-claim query shapes | Flat query cost; worker throughput ceiling (§8) becomes the binding constraint before table-size cost does | **ACCEPTABLE** at 10x, **POTENTIAL** bottleneck via worker throughput at 100x+ if event rate — not just table size — grows proportionally |
| TaskInstance | `GetMyTasksAsync` unbounded, unindexed `CreatedAt` sort (§5) | Fine for a typical user | A long-tenured heavy user's own task history could become genuinely large with no limit — **POTENTIAL**, driven by per-user count, not table size |
| ProcessInstance | Well-indexed for `ProcessMonitoringQueryService`'s filters | Flat cost for indexed filters; `ILike` search cost grows with row count | **ACCEPTABLE** for indexed access, **POTENTIAL** for search-heavy Monitoring/Reporting usage |
| FormInstance/TaskSla | Fully indexed on actual access patterns (unique `TaskInstanceId`, `(Status, DueAt/WarningAt)`) | Flat cost | **ACCEPTABLE** |
| Analytics (aggregate cost, not a table) | Measured 1.1s / 676KB at ~1.4k instances (§3) | Cost likely grows materially — the exact driver (which query, which aggregation) needs the query-count-oriented investigation recommended in §17 | **POTENTIAL → CONFIRMED-worth-investigating**, the single most concrete capacity risk found |

1000x was not separately analyzed — no evidence in this discovery suggests a materially different
conclusion would emerge beyond what the 100x row already shows (the worker throughput ceiling and
the Analytics cost are the two findings whose severity genuinely compounds with scale; everything
else stays flat due to existing indexing).

---

## 14. Confirmed Issues

1. `AnalyticsQueryService.BuildProcessComparisonAsync` — unfixed 2N+1 (per-process-definition loop, 2 queries each).
2. Pagination `int`-overflow bug (same class as the Phase 7.2.3 fix) unfixed in `AuditLogQueryService`, `ProcessDefinitionService`, `TaskQueryService.GetApprovalWorklistAsync`, `NotificationService`.
3. `TaskQueryService.GetMyTasksAsync` has no pagination at all.
4. `/api/analytics/overview` measured ~100x slower than every other endpoint tested (1.1s vs <30ms), 676KB payload, at real accumulated dev-scale data.
5. `/api/users` measured 587KB unpaginated payload at 2,139 rows.
6. No global TanStack Query cache defaults — `staleTime: 0`, `refetchOnWindowFocus: true` everywhere.
7. Frontend ships as a single unsplit ~1.78MB JS bundle, no route-based code splitting.
8. `TaskInstance` has no index on `CreatedAt` (the column `GetMyTasksAsync` sorts by).
9. 4 leading-wildcard `ILike` search call sites with no supporting trigram index.
10. No Docker container resource limits (`mem_limit`/`cpus`) on any service.

## 15. Potential Issues

- Worker throughput ceiling: `EmailDeliveryWorker` reasoned (not measured) to be the more likely bottleneck at extreme scale, since it absorbs 100% of SLA-worker output plus its own unconditional event surface.
- Container resource starvation under real load — no evidence of actual contention today.
- Analytics' unbounded date-range query parameter — no evidence anyone actually requests a multi-year range.
- `ILike` search cost growth as tables scale — currently harmless.
- `TaskInstance` Seq Scan on `AssigneeId` filter at current small scale — inconclusive `EXPLAIN` evidence, likely resolves itself at larger scale.

## 16. Acceptable Design Decisions

- EF's standard `Count` + `Skip/Take` pagination double-scan — a normal, accepted cost model, not a bug.
- `AsNoTracking` omission on Dashboard's `Count`-only aggregate queries — harmless, never materializes tracked entities regardless.
- Analytics' C#-side date bucketing — already justified in-code (no translatable Npgsql `DateTrunc`), operates only over already-filtered, timestamp-only projections.
- Administration's "fetch everything, paginate client-side" pattern for Users/Departments/Roles/SLA-Policies — deliberate, established design; §3's measured 587KB cost for Users is real but not yet a problem at 2,139 rows.
- Reporting CSV export buffering (bounded to 10,000 rows, ~2-5MB) and Attachment upload buffering (bounded to 25MB) — both bounded, reasonable at their caps.
- `AsSplitQuery` non-use — its Cartesian-explosion risk is not currently reachable anywhere in this codebase.
- Zero blocking I/O anywhere — confirmed clean, fully async.

## 17. Recommended Phase 12 Scope

**No major performance refactor is required.** The evidence points to a narrow, mostly query-level
scope:
1. Investigate `/api/analytics/overview`'s actual query-count/cost breakdown (the single most
   concrete measured finding) — likely candidates from §5/§13 are `BuildProcessComparisonAsync`'s
   2N+1 and/or the overall number of distinct queries `GetOverviewAsync` composes; a query-count
   interceptor (the same tool Phase 10's own regression test already uses) would identify the exact
   driver before any fix is written.
2. Fix `BuildProcessComparisonAsync`'s 2N+1 the same way `BuildNodeAnalyticsAsync` was fixed
   (batch the two per-item queries).
3. Apply the same `int`-overflow-safe skip-calculation fix (long arithmetic, clamped) to the four
   services still missing it.
4. Add real pagination to `TaskQueryService.GetMyTasksAsync`.
5. Add an index on `TaskInstance.CreatedAt`.
6. Set sane `QueryClient` default options (`staleTime`, `refetchOnWindowFocus`) once, globally.
7. Introduce route-based code splitting (`React.lazy`) for the largest, least-frequently-visited
   routes (Designer, Analytics, Administration) to reduce first-paint bundle size.

Everything else in §14-16 is either already acceptable at current/near-term scale or lacks
sufficient evidence to justify action now.

## 18. MUST / SHOULD / NICE / DEFER

**MUST**: none — no finding rises to "must fix now" by the brief's own strict bar (nothing here
causes data loss, an outage, or a security issue; the worst case is a slow page or an occasional
raw-error response on an extreme, unlikely input).

**SHOULD**:
- Investigate + fix the Analytics endpoint's actual cost driver (§14.4, the clearest measured evidence).
- Fix the 4 unpatched pagination `int`-overflow sites (§14.2) — same bug class already known to be worth fixing once; low-risk, mechanical.
- Fix `BuildProcessComparisonAsync`'s 2N+1 (§14.1) — same proven pattern as the already-shipped fix.
- Add pagination to `GetMyTasksAsync` (§14.3).
- Set `QueryClient` defaults (§14.6) — trivial, real, low-risk.

**NICE**:
- Add the `TaskInstance.CreatedAt` index (§14.8).
- Route-based code splitting (§14.7).
- A `pg_trgm` trigram index for the 4 `ILike` search sites (§14.9) — real, but not yet costing anything measurable.

**DEFER**:
- Redis / any caching layer.
- Docker container resource limits (§14.10) — no evidence of actual contention yet; revisit if it becomes observable.
- Keyset pagination anywhere — no evidence of a large-offset use case.
- Worker batch-size/poll-interval tuning — no evidence current settings are actually a bottleneck yet.
- Any schema/architecture change.

## 19. Suggested Validation Plan

If Phase 12 implementation proceeds: reuse the exact pattern Phase 10 already established —
`DbCommandInterceptor`-based query-count regression tests (as already used for the Analytics N+1
fix), plus the same non-destructive live-benchmark approach used in this discovery (single-request
timing against the real Docker stack, before/after comparison) to confirm the Analytics fix
actually reduces the measured 1.1s. After any implementation: re-run the full existing suite
(Backend 484/484, Frontend 362/362, Live 42/42) — since this discovery made zero code changes,
these baseline numbers stand unmodified and unverified-again-here (no code was touched).

## 20. Explicitly Deferred Items

Everything in §18's DEFER list, plus, per the brief's own explicit instruction: no Redis/distributed
cache, no message queue/event bus, no microservices, no CQRS, no Elasticsearch, no new monitoring
subsystem, no keyset-pagination rewrite, no new state-management library, no UI framework change —
none of these were introduced, and none are recommended by the evidence gathered.

---

**PHASE 12 ARCHITECTURE DISCOVERY COMPLETE.**

---

## 21. Implementation Result (Phase 12 Implementation)

Everything in this section was added after Discovery was declared complete above; §1-§20 are left
unmodified as the original evidence record. Scope executed: the 5 SHOULD items (§18) plus the one
explicitly-approved NICE item (`TaskInstance.CreatedAt` index). Code splitting (the other NICE
item) and the `pg_trgm` index were both left DEFERRED, per the brief's own instruction to implement
NICE items only if a separate explicit approval named them.

### 21.1 Analytics Overview — root cause confirmed, fixed, re-measured

- **Root cause**: `AnalyticsQueryService.BuildProcessComparisonAsync` issued 2 queries per distinct
  `ProcessDefinitionId` in scope (a classic 2N+1) — with 987 distinct definitions in the dev DB
  (confirmed via a live `SELECT COUNT(DISTINCT "ProcessDefinitionId")`), that's ~1,975 queries in
  one request. This was §14.4/§14.1's own top suspect, now directly confirmed as the actual driver:
  fixing only this one method dropped the endpoint from ~1.1s to ~30-50ms end to end — nothing else
  needed further investigation per the brief's own "keep investigating only if this isn't the
  driver" rule.
- **Fix**: replaced the per-definition loop with two single bulk queries (durations, SLA rows),
  grouped in-memory via `Dictionary` lookups — the same bounded-follow-up-query shape
  `BuildNodeAnalyticsAsync` already used since Phase 10.
- **Before**: ~1.1s, 675,822 bytes (Discovery baseline, single live measurement).
- **After**: 3 consecutive live measurements against the rebuilt Docker stack:
  `0.052s` / `0.029s` / `0.026s` / `0.047s`, 778,007 bytes (byte count differs from Discovery's
  because the dev DB has continued accumulating rows since that measurement was taken — not a
  regression, no data was removed or hidden).
- **Query count**: a new `DbCommandInterceptor`-based regression test
  (`ProcessComparison_QueryCount_DoesNotScaleWithNumberOfDistinctProcessDefinitions`, mirroring the
  existing `NodeAnalytics_...` test) proves the query count is now identical whether there is 1 or
  5 distinct process definitions in scope — structurally 2N+1-free, not just faster on today's data.

### 21.2 Pagination `int`-overflow fixes (4 sites)

`AuditLogQueryService`, `ProcessDefinitionService.GetAllAsync`, `TaskQueryService
.GetApprovalWorklistAsync` all got the exact Phase 7.2.3 `ProcessMonitoringQueryService` pattern:
`var skip = (int)Math.Min((long)(page - 1) * pageSize, int.MaxValue);`. Each has a dedicated
`ExtremelyLargePage_DoesNotThrow_ReturnsEmptyResult` test. Verified live against the rebuilt stack
at `Page=2147483647` on all four endpoints (plus the new `GetMyTasksAsync`, below) — all return
`200` with an empty page, never a raw Postgres exception.

### 21.3 `GetMyTasksAsync` pagination

Previously unbounded. Now takes `MyTasksQuery(Page, PageSize)` (default `PageSize=200`, matching
the endpoint's existing "no dedicated pagination UI yet" reality rather than the usual 20), returns
`PagedResult<TaskDto>` — the same envelope `GetApprovalWorklistAsync` already used, not a new DTO
shape. Backend pushes `Skip`/`Take` to the database over the existing `BuildMyTaskIdsQuery` id
union (left untouched — still shared with `DashboardQueryService`). `GET /api/tasks`'s response
shape changed from a bare array to `PagedResult<TaskDto>`; the frontend's `taskService.ts
.listMyTasks()` was updated to request `page=1&pageSize=200` and unwrap `.items`, so every existing
consumer (`useMyTasks`, `TasksPage.tsx`) needed zero changes. Confirmed live: a fresh non-admin user
with zero tasks gets `{"items":[],"totalCount":0,...}`, and admin's own worklist paginates
correctly (`totalCount: 284`, 2-item page returned 2 items).

### 21.4 `TaskInstance.CreatedAt` index

Confirmed via inspection (`TaskInstanceConfiguration.cs`) no existing index covered `CreatedAt`
before this phase (only `AssigneeId`/`Status`/`DueAt` did) — added as a single-column index,
matching the file's own existing pattern, supporting `GetMyTasksAsync`'s new `ORDER BY CreatedAt
DESC`. One migration, `AddTaskInstanceCreatedAtIndex`, additive and reversible
(`Down` drops the index), applied cleanly to both `bpm` (dev) and `bpm_test` (via the test suite's
own auto-migration).

### 21.5 TanStack Query global defaults

`frontend/src/App.tsx`'s `QueryClient` previously had zero options (`staleTime: 0`,
`refetchOnWindowFocus: true` — the Discovery-identified gap). Now: `staleTime: 30_000`,
`refetchOnWindowFocus: false`, `refetchOnReconnect: true`, `retry: 1`. Chosen conservatively for a
read-heavy, non-realtime app; every per-query override already present in the codebase (most
notably `useUnreadCount`'s own 30s `refetchInterval` poll) is untouched and still takes precedence.

### 21.6 Frontend code splitting — DEFERRED

Reviewed, not implemented. Route-based `React.lazy`/`Suspense` would require wrapping the entire
router in a `<Suspense>` boundary with a loading fallback — a change to loading UX across every one
of the ~19 routes in `App.tsx`, not an isolated one. The brief's own instruction was to implement
only if genuinely low-risk/isolated ("no large loading-state changes") and otherwise defer — this
didn't clear that bar, so it stays deferred, exactly as §18's NICE classification anticipated.

### 21.7 Regression evidence

- Backend: 492/492 (484 baseline + 8 new: 1 Analytics 2N+1 query-count test, 4 extreme-page tests
  across `AuditLogQueryService`/`ProcessDefinitionService`/`GetApprovalWorklistAsync`/
  `NotificationService`, 3 new `GetMyTasksAsync` tests — pagination correctness, extreme-page
  safety, authorization regression).
- Frontend: 362/362 unchanged (no test needed new assertions — `listMyTasks()`'s internal
  unwrapping kept its public contract as `Task[]`, and every existing mock already mocked
  `listMyTasks` itself rather than the raw HTTP shape).
- Live: 42/42, 3 consecutive clean runs. One transient failure was observed and diagnosed on a
  retry (`expenseE2E.live.test.ts`, a pre-existing `Date.now()`-only username-collision flake under
  this suite's parallel execution — same known class of issue Phase 6.4 already fixed in two other
  files, not present in this one) — confirmed unrelated to this phase's changes by re-running that
  file alone (passed), then re-running the full suite twice more clean. Not fixed here (unrelated
  refactor, out of this phase's scope) — flagged for a future phase.
- A real regression *was* found and fixed during this work, not by chance: ~40 live test call sites
  across 20 files called `GET /api/tasks` directly via raw HTTP and destructured the response as a
  bare array (`tasks.data.find(...)`) — the exact "confirm actual frontend/test usage" step the
  brief required before changing this contract. All were updated to `tasks.data.items.find(...)`.
- Migration verified against both `bpm` (dev, `dotnet ef database update`) and `bpm_test` (implicit,
  via the test suite's own migration-on-demand fixture).
- Docker: `bpm-api`/`bpm-web` rebuilt and recreated; all 5 containers (`postgres`, `redis`, `minio`,
  `bpm-api`, `bpm-web`) confirmed running/healthy; workers (`SlaSchedulerWorker`,
  `EmailDeliveryWorker`) confirmed still ticking in `bpm-api` logs after rebuild.
- Security/business-semantics spot-checks against the live rebuilt stack: a fresh non-admin user
  sees `totalCount: 0` on their own My Tasks and `403` on admin-only Audit Logs; Phase 10's
  `PROCESS_INSTANCE_DUPLICATE_BUSINESS_KEY` idempotency still fires correctly on a duplicate
  business key.
- Browser verification: unavailable — the Claude-in-Chrome extension was not connected in this
  session, consistent with every prior phase.

**PHASE 12 PERFORMANCE & SCALABILITY IMPLEMENTATION COMPLETE.**
