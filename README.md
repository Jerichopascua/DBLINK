# CBMSB2BLink

A small, configuration-driven .NET tool that copies new rows from one or more source
databases into their own target tables on a schedule — plus a read-only dashboard for
watching that it's actually happening. It has no built-in knowledge of any particular
table or business domain: every sync is just an entry in config pointing at a source
query and a target table.

## What it does

- Runs a list of independent **sync jobs** (`Sync:Jobs` in config). Each job:
  1. Calls a source stored procedure (or raw SQL), paging through results with
     `@LastRowId`/`@BatchSize`.
  2. Bulk-copies whatever comes back into a target table, positionally — no
     per-job code, no hardcoded column model.
  3. Resumes where it left off next time via a small per-job watermark table in
     its own target database, and records what happened in an audit table.
- One job failing doesn't stop the others in the same run.
- A separate, read-only ASP.NET Core dashboard reports each job's health and
  recent run history — no shared state with the sync tool beyond the database
  it reads from.

Adding a new sync means writing a source stored procedure that matches the
contract below and adding one JSON block to config — see
[docs/CONFIGURATION.md](docs/CONFIGURATION.md) for the full walkthrough.

## Project layout

| Project | What it is |
|---|---|
| `CBMSB2BLink.Core` | Domain models, config options, repository interfaces, and the sync engine itself — no I/O. |
| `CBMSB2BLink.Data` | SQL Server implementations of the Core interfaces (Dapper + `Microsoft.Data.SqlClient`, `SqlBulkCopy`). |
| `CBMSB2BLink.Console` | The scheduled, one-shot executable — composition root, config binding/validation, logging, email alerts, run-lock. |
| `CBMSB2BLink.Monitoring.Api` | Read-only ASP.NET Core dashboard/API over each job's run history. Hosted separately; nothing else depends on it. |
| `CBMSB2BLink.Tests` | xUnit tests for the sync engine and the dashboard's health logic, against mocked dependencies. |

## Requirements

- .NET 6 SDK
- SQL Server for each job's source and target databases (can be the same instance/server)

## Quick start

```powershell
# Build everything
dotnet build

# Run the sync tool once (reads src/CBMSB2BLink.Console/appsettings.json)
dotnet run --project src/CBMSB2BLink.Console

# Run the tests
dotnet test src/CBMSB2BLink.Tests

# Run the monitoring dashboard locally
dotnet run --project src/CBMSB2BLink.Monitoring.Api
```

Before the first run, edit `src/CBMSB2BLink.Console/appsettings.json` with real
connection strings and at least one job, and make sure that job's source proc and
target table exist. `dotnet run` will fail fast at startup with a clear message if
required config is missing.

## Configuration

Every sync job lives entirely in `appsettings.json` — connection strings, the source
command, the target table/columns, and per-job tuning knobs (page size, per-run row
cap, timeouts). See [docs/CONFIGURATION.md](docs/CONFIGURATION.md) for the full
settings reference and a step-by-step "add a new job" guide.

## Docs

| Doc | Covers |
|---|---|
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | How a run actually works end-to-end, the resume watermark, failure/recovery behavior, capacity limits. |
| [docs/CONFIGURATION.md](docs/CONFIGURATION.md) | Full `appsettings.json` reference and how to add a new sync job. |
| [docs/RUNBOOK.md](docs/RUNBOOK.md) | Day-to-day operation: manual runs, checking status, re-syncing a gap. |
| [docs/TESTING.md](docs/TESTING.md) | Local end-to-end testing against scratch databases, no real source/target access needed. |
| [docs/PRODUCTION_SETUP.md](docs/PRODUCTION_SETUP.md) | Deploying and scheduling the tool for real. |

## Status and history

Each job's target database gets two small tables the tool manages itself (created
automatically on first run — nothing to script by hand):

- `SyncRunHistory` — an append-only audit log of every run (status, row ranges,
  duration, errors). This is what the dashboard reads.
- `CbmsB2BLink_ResumeCursor` — one row per job, holding the next run's starting
  position. Ops can update this by hand at any time to force a resume point.
