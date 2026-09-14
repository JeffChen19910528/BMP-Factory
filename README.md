# BPM Platform

An enterprise Business Process Management platform — a real, configuration-driven Workflow
Engine, not a hardcoded "form + approval" app. See `Skill.md` for the full specification and
`CLAUDE.md` for architecture notes aimed at anyone (human or AI) working in this repo.

## Status

Phase 1 (Foundation) and Phase 2 (Workflow Core) are implemented and verified end-to-end against
a live PostgreSQL container. See `PROGRESS.md` for exact scope and known gaps.

## Getting started

Prerequisites: .NET 10 SDK, Docker Desktop.

```bash
# From the repo root — brings up postgres, redis, minio, and the API.
JWT_SECRET="<a real secret, 32+ chars>" docker compose up -d postgres redis minio bpm-api
```

The API auto-applies EF Core migrations and seeds an `Administrator` role plus an `admin` /
`ChangeMe123!` bootstrap account on first startup. It listens on `http://localhost:5080`; Swagger
UI is at `http://localhost:5080/swagger` in Development.

`bpm-web` (the frontend) isn't scaffolded yet, so don't include it in `docker compose up` until
`frontend/` exists — the build will fail with "path not found".

To develop without Docker for the API itself: run `docker compose up -d postgres redis minio`,
then `dotnet run --project src/BPM.Api` (see `CLAUDE.md` for the full command list, including EF
Core migration commands).

## Example: define and run a workflow

Everything here is done through the API — no process is ever hardcoded into the backend.

```bash
TOKEN=$(curl -s -X POST http://localhost:5080/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"admin","password":"ChangeMe123!"}' | jq -r .accessToken)

# 1. Create a process definition
curl -s -X POST http://localhost:5080/api/process-definitions \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"key":"leave-request","name":"Leave Request"}'

# 2. Add a draft version: a Start -> UserTask -> End graph, assigned to a role
curl -s -X POST http://localhost:5080/api/process-definitions/<id>/versions \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{
    "definition": {
      "nodes": [
        {"id":"start","type":"Start","name":"Start"},
        {"id":"approval","type":"UserTask","name":"Manager Approval","assignment":{"type":"Role","value":"Manager"}},
        {"id":"end","type":"End","name":"End"}
      ],
      "transitions": [
        {"id":"t1","source":"start","target":"approval"},
        {"id":"t2","source":"approval","target":"end"}
      ]
    }
  }'

# 3. Publish it (validated — publish is rejected if the graph is malformed)
curl -s -X POST http://localhost:5080/api/process-definitions/<id>/publish -H "Authorization: Bearer $TOKEN"

# 4. Start an instance
curl -s -X POST http://localhost:5080/api/process-instances \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"processDefinitionKey":"leave-request","businessKey":"LR-0001"}'

# 5. Whoever holds the "Manager" role sees the task and completes it
curl -s http://localhost:5080/api/tasks -H "Authorization: Bearer $MANAGER_TOKEN"
curl -s -X POST http://localhost:5080/api/tasks/<taskId>/complete -H "Authorization: Bearer $MANAGER_TOKEN"
```

## Documentation map

- `Skill.md` — the governing specification (target architecture, all phases).
- `CLAUDE.md` — condensed architectural rules and dev commands for anyone working in this repo.
- `PROGRESS.md` — what's actually built vs. spec, phase by phase.
- `CHANGELOG.md` — notable changes.
