# Deployment Guide

This document covers what's needed to run the BPM Platform outside a developer's own machine:
required configuration, startup behavior, health checks, logging, backup/restore, and common
troubleshooting. It complements `README.md` (what the system is and how to run it locally) rather
than replacing it.

## Prerequisites

- Docker and Docker Compose (the only supported deployment path today — there is no Kubernetes
  manifest, Helm chart, or other orchestration tooling in this repository).
- A reachable PostgreSQL-compatible database (the bundled `postgres` service, or an external
  PostgreSQL 16+ instance — nothing PostgreSQL-specific beyond what `Npgsql.EntityFrameworkCore
  .PostgreSQL` needs).
- An S3-compatible object store for attachments (the bundled MinIO service, or any real S3-
  compatible endpoint).
- Redis is provisioned in `docker-compose.yml` but is **not currently used anywhere in the
  application** (confirmed by Phase 10's own Architecture Discovery — no code path references
  it). It can be left running as provisioned, or omitted from a production deployment entirely;
  nothing depends on it.

## Required environment variables

| Variable | Required | Notes |
|---|---|---|
| `JWT_SECRET` | **Yes** | `docker-compose.yml` fails fast (`JWT_SECRET must be set`) if this isn't set. Must be at least 32 characters. Never commit a real value — the compose file only ever reads it from the environment. |

Everything else `bpm-api` needs (database connection string, MinIO credentials, email settings,
SLA scheduler settings) is already set as plain environment variables in `docker-compose.yml`
using the ASP.NET Core `Section__Key` double-underscore convention, and mirrored with local
defaults in `src/BPM.Api/appsettings.json` for running the API directly with `dotnet run`.

**The credentials committed in `docker-compose.yml` (`bpm`/`bpm`, `bpm`/`bpm12345`) are
dev/test-only.** They exist so a fresh `docker compose up` works with zero manual setup. A real
deployment must override every one of them — database password, MinIO access/secret key, and any
SMTP credentials if `Email__Provider` is switched from `Fake` to `Smtp` — with values injected via
the deployment environment's own secret management (environment variables, a mounted secrets
file, or your orchestrator's native secrets mechanism), never committed to source control or
written into this document.

## Database (PostgreSQL)

- Connection string: `ConnectionStrings__Default` (compose) / `ConnectionStrings:Default`
  (appsettings.json).
- **Migrations apply automatically on every `bpm-api` startup** (`DbSeeder.SeedAsync` calls
  `Database.MigrateAsync()` before the API starts accepting traffic — see `Program.cs`). There is
  no separate "run migrations" step to remember. This also means `bpm-api` will not become healthy
  until migration has finished, which is expected — a first boot against a large existing database
  may take longer than a fresh one.
- A fresh database is seeded with an `Administrator` role and a bootstrap `admin` /
  `ChangeMe123!` account. **Change this password immediately in anything beyond local dev** — via
  the Administrator password-reset flow (`POST /api/users/{id}/reset-password`) or the
  self-service change-password flow, both covered in `CLAUDE.md`'s System Administration section.
- Every migration in this project's history is additive-only (new tables/columns/indexes, never a
  destructive schema change) — an upgrade deployment (existing data, new migrations) uses the
  exact same `MigrateAsync()` startup path as a fresh deployment; there is no separate upgrade
  procedure.

## Object storage (MinIO / S3-compatible)

- Configuration: `Minio__Endpoint`, `Minio__AccessKey`, `Minio__SecretKey`, `Minio__BucketName`,
  `Minio__UseSsl`.
- The application connects lazily — `MinioAttachmentStorage` only actually talks to the object
  store on the first real attachment upload/download, creating the configured bucket if it
  doesn't already exist. `bpm-api` itself depends on MinIO's own Docker healthcheck
  (`docker-compose.yml`) before starting, so a fresh `docker compose up` won't race MinIO's own
  startup — see the Health Endpoints section below.
- Bytes never live in PostgreSQL — only attachment metadata (filename, content type, size, hash,
  storage key) does. The object store is where the actual file content is.

## Email

- `Email__Enabled`, `Email__Provider` (`Fake` or `Smtp`), plus `Host`/`Port`/`Username`/`Password`/
  `UseSsl`/`FromAddress`/`FromName` when `Provider=Smtp`.
- The bundled compose stack runs `Provider=Fake` (no real mail server in this stack). **A real
  deployment must explicitly set `Provider=Smtp` with real credentials** — nothing here ever
  silently attempts a real SMTP connection unless configured to.
- Delivery is asynchronous via `EmailDeliveryWorker` (a `BackgroundService`) — the API itself never
  blocks a request on sending an email.

## Docker startup

```
JWT_SECRET=<32+ char secret> docker compose up -d
```

Brings up `postgres`, `redis`, `minio`, `bpm-api`, `bpm-web`. Startup ordering:
1. `postgres` and `minio` must both report healthy (Docker healthchecks) before `bpm-api` starts.
2. `bpm-api` runs migrations + seeding, starts its two background workers
   (`EmailDeliveryWorker`, `SlaSchedulerWorker`), then starts accepting traffic.
3. `bpm-web` depends on `bpm-api` being up (not necessarily healthy — it's a static nginx-served
   SPA that calls the API from the browser, not at its own startup).

### Health endpoints

- `GET /health` — overall health (checks PostgreSQL connectivity).
- `GET /health/live` / `GET /health/ready` — liveness/readiness probes.
- `GET /api/operational-health` (Administrator-only, requires a JWT) — a richer diagnostic view:
  database/object-storage connectivity, notification delivery backlog, and a handful of
  non-secret configuration values (never passwords, keys, or connection strings). See
  `CLAUDE.md`'s System Administration section for exactly what it does and doesn't expose.

A container reporting "running" is not the same as the application inside it being healthy —
use these endpoints (or `docker compose ps`, which reflects the Docker-level healthchecks
configured for `postgres`/`minio`) rather than assuming a running container is a working one.

### Background workers

`EmailDeliveryWorker` and `SlaSchedulerWorker` are both `BackgroundService`s hosted inside the
`bpm-api` process itself — there is no separate worker container or process to deploy. Both poll
on an interval (`Email__PollIntervalSeconds`, `SlaScheduler__PollIntervalSeconds`) and log a
one-line summary each tick (`"EmailDeliveryWorker processed N pending delivery(ies)"`, etc.) —
visible in `docker compose logs bpm-api` or the durable log files described below. Both use
database-backed conditional claiming (not in-memory state), so a `bpm-api` restart never loses
in-flight work — a claim left in `Processing` by a killed worker becomes reclaimable again after
its own stale-claim timeout, and an SLA transition is wrapped in an explicit transaction that
rolls back cleanly if interrupted mid-tick.

## Logging

- Structured logging via Serilog, configured entirely from `appsettings.json`'s `Serilog` section
  (`ReadFrom.Configuration` — no code change needed to adjust sinks/levels).
- **Two sinks are active**: Console (unchanged, visible via `docker compose logs bpm-api`) and a
  rolling **File** sink writing to `logs/bpm-api-<date>.log` inside the container, mounted to the
  `api_logs` named Docker volume so log files survive a container recreate. Rolls daily or at 50MB
  (whichever comes first), retaining the most recent 14 files — bounded, not unbounded disk growth.
- To read the durable logs from the host: `docker compose exec bpm-api ls /app/logs` and `docker
  compose exec bpm-api cat /app/logs/<file>`, or inspect the named volume directly
  (`docker volume inspect bmp-factory_api_logs`).
- Nothing in this codebase logs secrets — confirmed by inspection, not merely by convention: no
  `_logger.Log*` call anywhere references `Jwt:Secret`, a password (plaintext or hashed),
  `Minio:SecretKey`, SMTP credentials, or the database connection string. `ErrorHandlingMiddleware`
  itself never logs at all — it only ever returns the structured `{code, message, traceId}` body.

## Backup / Restore

There is no backup automation, scheduler, or dedicated backup service in this repository —
deliberately not built without an actual requirement driving its shape. What follows is an
**operational procedure**, not implemented tooling, for the two systems that together hold all of
this platform's durable state: **PostgreSQL** (everything except file bytes) and **MinIO**
(attachment file bytes only — metadata about them lives in PostgreSQL).

### Why both matter together

An `Attachment` row in PostgreSQL is only meaningful together with the MinIO object its
`StorageKey` points at. Backing up only one of the two is backing up half the system:

- **PostgreSQL backed up, MinIO not restored to the same point in time**: `Attachment` rows exist
  pointing at objects that may not exist (if the object was created after the Postgres backup) or
  differ from what the row's hash expects. A download of such an attachment fails cleanly with a
  structured `ATTACHMENT_CONTENT_UNAVAILABLE` error (Phase 10 hardened `AttachmentService
  .DownloadAsync` specifically so this never leaks a raw storage exception) — a real, visible
  failure for that one attachment, not silent data corruption, and not something that affects any
  other part of the system.
- **MinIO restored, PostgreSQL not restored to the same point in time**: orphaned objects sit in
  the bucket with no `Attachment` row referencing them — wasted storage, not a correctness
  problem, and not currently detected or cleaned up automatically (no retention/reconciliation job
  exists in this codebase — see `CLAUDE.md`'s Phase 10 Discovery notes).

**Recommendation**: back up both together, as close to the same instant as practical, and restore
both together to the same backup set. Treat a PostgreSQL-only or MinIO-only restore as a known,
partial recovery — usable, but with the specific gaps above.

### PostgreSQL backup

```
docker compose exec -T postgres pg_dump -U bpm -d bpm --format=custom --file=/tmp/bpm-backup.dump
docker compose cp postgres:/tmp/bpm-backup.dump ./bpm-backup-$(date +%Y%m%d-%H%M%S).dump
```

(`--format=custom` produces a compressed, `pg_restore`-only file — smaller and more flexible than
plain SQL for a database this shape. Substitute your own external `pg_dump`/connection details if
PostgreSQL isn't the bundled compose service.)

### PostgreSQL restore

```
docker compose cp ./bpm-backup-<timestamp>.dump postgres:/tmp/bpm-restore.dump
docker compose exec -T postgres dropdb -U bpm bpm
docker compose exec -T postgres createdb -U bpm bpm
docker compose exec -T postgres pg_restore -U bpm -d bpm /tmp/bpm-restore.dump
```

Then start (or restart) `bpm-api` — it will apply any migrations newer than the backup
automatically on startup, the same as any other startup (see the Database section above).

### MinIO backup (mirroring)

The MinIO Client (`mc`) is the tool MinIO itself documents for this — it isn't bundled in this
repo's images, but is a single static binary:

```
mc alias set bpm-source http://localhost:9000 bpm bpm12345
mc mirror bpm-source/bpm-attachments ./minio-backup-$(date +%Y%m%d-%H%M%S)/
```

For an external/production MinIO or S3-compatible endpoint, point `mc alias set` at that
endpoint's own URL and credentials instead.

### MinIO restore

```
mc alias set bpm-target http://localhost:9000 bpm bpm12345
mc mirror ./minio-backup-<timestamp>/ bpm-target/bpm-attachments
```

### Ordering and consistency

Take both backups as close together as practical (e.g., pause attachment uploads briefly, or
accept the small inconsistency window an unpaused system always has — this platform does not use
distributed transactions between PostgreSQL and MinIO in normal operation either, so a brief
window of the same class of inconsistency already exists day to day; see `AttachmentService`'s own
hardening notes). Restore PostgreSQL first, then MinIO, then start `bpm-api` last.

### Verification after restore

1. `GET /health` returns `Healthy`.
2. `GET /api/operational-health` (as an Administrator) shows `Database: Healthy` and
   `ObjectStorage: Healthy`.
3. Log in as a known pre-backup user; confirm their data (processes, tasks, notifications) is
   present as expected.
4. Open a process instance known to have an attachment from before the backup; download it and
   confirm it opens correctly (proves both halves of the backup are consistent for that record).
5. Check `docker compose logs bpm-api` (or the durable log files) for any startup errors.

### Recommended cadence (recommendation only — not automated)

A nightly full backup of both PostgreSQL and MinIO, retained for at least the same window as this
platform's own data (there is currently no data-retention/expiry policy — see `CLAUDE.md`'s Phase
10 Discovery notes — so backups should be retained at least as long as the data itself is expected
to matter). This is a starting recommendation for an operator to adopt, size, and automate
according to their own environment's actual RPO/RTO requirements — not something this repository
implements.

## Common troubleshooting

| Symptom | Likely cause | What to check |
|---|---|---|
| `bpm-api` container exits immediately | `JWT_SECRET` not set | `docker compose up` output shows the fail-fast message directly. |
| `bpm-api` never becomes healthy | PostgreSQL or MinIO not yet healthy | `docker compose ps` — `bpm-api` won't even start until both report healthy. |
| Login fails for every account after a fresh deploy | Seeding didn't run / database wasn't actually fresh | Check `docker compose logs bpm-api` for `DbSeeder` output; confirm the `admin` bootstrap account exists in the `Users` table. |
| Attachment upload/download fails with `ATTACHMENT_STORAGE_UNAVAILABLE` or `ATTACHMENT_CONTENT_UNAVAILABLE` | MinIO unreachable, or (for downloads) the object is genuinely missing | `GET /api/operational-health`'s `ObjectStorage` field; confirm the MinIO container is healthy and the configured bucket exists. |
| Emails never send | `Email__Provider` still `Fake`, or `Email__Enabled=false` | Confirm both settings; check `EmailDeliveryWorker`'s own tick logs for claimed/sent counts. |
| SLA warnings/escalations never fire | `SlaScheduler__Enabled=false`, or no `SlaPolicy` configured for the node | `GET /api/operational-health`'s configuration section shows whether the scheduler is enabled (configuration only, not a live "is it currently ticking" guarantee — see `CLAUDE.md`'s own note on this distinction). |
| A 500 response with no `code`/`message`/`traceId` body | A genuinely unhandled exception — `ErrorHandlingMiddleware` only maps `AppException`/FluentValidation's `ValidationException` by design | Check the durable logs for the real exception; this is intentionally not hidden from server-side logs, only from the client response. |
