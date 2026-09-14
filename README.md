# BPM Platform

An enterprise Business Process Management platform — a real, configuration-driven Workflow
Engine, not a hardcoded "form + approval" app.

## Status

Phase 1 (Foundation), Phase 2 (Workflow Core), Phase 3 (Approval Engine), and Phase 4 (Form
Engine) are implemented and verified end-to-end against live PostgreSQL and MinIO containers.

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
then `dotnet run --project src/BPM.Api` from `src/`.

```bash
dotnet build                                   # build the whole solution
dotnet test                                    # run all tests (needs Postgres reachable at
                                                # localhost:5432 for the integration tests; they
                                                # create/migrate their own "bpm_test" database)

# EF Core migrations (dotnet-ef installed as a global tool: dotnet tool install --global dotnet-ef)
dotnet ef migrations add <Name> --project BPM.Infrastructure --startup-project BPM.Api -o Persistence/Migrations
dotnet ef database update --project BPM.Infrastructure --startup-project BPM.Api
```

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

## Example: an approval workflow (Phase 3)

`ApprovalTask` nodes support Sequential/All/AnyOne policies over multiple resolved approvers
(Role/Department resolve to every member; User/ProcessInitiator/DepartmentManager resolve to one).
Use `POST .../approve`, `/reject`, `/return`, `/delegate`, `/transfer`, `/approvers` instead of
`/complete` for these tasks.

```bash
# An "All" gate requiring one approver from each of three roles — the process stays Running
# until Finance, Legal, and IT have all individually approved the same task.
curl -s -X POST http://localhost:5080/api/process-definitions/<id>/versions \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{
    "definition": {
      "nodes": [
        {"id":"start","type":"Start","name":"Start"},
        {"id":"approval","type":"ApprovalTask","name":"Expense Approval","approval":{
          "policy":"All",
          "assignments":[
            {"type":"Role","value":"Finance"},
            {"type":"Role","value":"Legal"},
            {"type":"Role","value":"IT"}
          ],
          "allowReject":true,"allowReturn":true,"allowDelegate":true,"allowTransfer":true,"allowAddApprover":true
        }},
        {"id":"end","type":"End","name":"End"}
      ],
      "transitions": [
        {"id":"t1","source":"start","target":"approval"},
        {"id":"t2","source":"approval","target":"end"}
      ]
    }
  }'

# Each approver acts independently on the same task id:
curl -s -X POST http://localhost:5080/api/tasks/<taskId>/approve -H "Authorization: Bearer $FINANCE_TOKEN"
curl -s -X POST http://localhost:5080/api/tasks/<taskId>/approve -H "Authorization: Bearer $LEGAL_TOKEN"
curl -s -X POST http://localhost:5080/api/tasks/<taskId>/approve -H "Authorization: Bearer $IT_TOKEN"
# -> only the third call flips the task (and process) to Completed.
```

Use `"policy":"AnyOne"` for "first approver wins, rest cancelled" gates, or `"policy":"Sequential"`
to require multiple resolved approvers to act one at a time in resolution order.

## Example: a form-driven task (Phase 4)

A `UserTask` can reference a form; submitting it completes the task and advances the workflow —
no form-specific code lives in the workflow engine itself.

```bash
# 1. Define and publish a form
curl -s -X POST http://localhost:5080/api/form-definitions \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"key":"purchase-request-form","name":"Purchase Request Form"}'

curl -s -X POST http://localhost:5080/api/form-definitions/<id>/versions \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{
    "schema": {
      "fields": [
        {"key":"itemName","type":"text","label":"Item Name","required":true},
        {"key":"quantity","type":"number","label":"Quantity","required":true,"validation":{"minValue":1}},
        {"key":"amount","type":"currency","label":"Amount","required":true,"validation":{"minValue":0}}
      ]
    }
  }'

curl -s -X POST http://localhost:5080/api/form-definitions/<id>/publish -H "Authorization: Bearer $TOKEN"

# 2. Reference it from a UserTask node when creating a process version
#    {"id":"submitRequest","type":"UserTask","name":"Submit Request",
#     "assignment":{"type":"ProcessInitiator","value":""},
#     "form":{"formDefinitionKey":"purchase-request-form"}}

# 3. Starting the process auto-creates a Draft FormInstance for that task. Fill it in and submit:
curl -s -X PUT http://localhost:5080/api/form-instances/<id>/data \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"data":{"itemName":"Server","quantity":1,"amount":75000},"expectedVersion":"<from GET .../data>"}'

curl -s -X POST http://localhost:5080/api/form-instances/<id>/submit -H "Authorization: Bearer $TOKEN"
# -> validates required fields, completes the UserTask, advances the workflow, locks the form —
#    all in one atomic call.

# Attachments (stored in MinIO, metadata only in Postgres):
curl -s -X POST http://localhost:5080/api/form-instances/<id>/attachments \
  -H "Authorization: Bearer $TOKEN" -F "file=@receipt.pdf"
curl -s http://localhost:5080/api/attachments/<attachmentId> -H "Authorization: Bearer $TOKEN" -o receipt.pdf
```

## Documentation map

- `README.md` (this file) — getting started, worked examples for each phase.
- `CHANGELOG.md` — notable changes.

This repo is developed with AI assistance; the specification, architectural-decision log, and
phase-by-phase build notes used during that process are kept locally (gitignored) rather than
published here.
