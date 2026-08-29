# CBMSB2BLink — Configuration

CBMSB2BLink runs a list of independent sync jobs (`Sync:Jobs`) — each one reads new
rows from its own source database/query and bulk-inserts them into its own target
table. There is no per-pipeline code to write: adding a sync means adding a job entry
to config (plus, on the source side, a stored procedure matching the contract below).
See `docs/superpowers/specs/2026-08-24-generic-sync-engine-design.md` for the design
this config shape implements.

## Settings reference (`appsettings.json`)

| Section | Key | Meaning | Default |
|---|---|---|---|
| `Sync` | `MaxRunDurationSeconds` | Whole-run cancellation timeout, covering every configured job in the run (not reset per job). | `1800` |
| | `LockFilePath` | Override for the run lock file (one lock covers the whole run, all jobs). Blank = `%ProgramData%\CBMSB2BLink\run.lock`. | `""` |
| | `Jobs` | Array of job objects (see below). At least one required. | — required |
| `Sync:Jobs[]` | `JobKey` | Identifies this job in `SyncRunHistory` (its `SyncKey` column) and in log lines/failure emails. | — required |
| | `Source.ConnectionString` | This job's source database connection string. | — required |
| | `Source.CommandText` | Stored procedure name, or raw SQL text when `CommandType` is `"Text"`. Must accept `@LastRowId BIGINT` and `@BatchSize INT`, and itself only return rows with a key greater than `@LastRowId` — CBMSB2BLink computes the resume cursor from `dbo.SyncRunHistory` (`MAX(SourceRowIdTo)` for the job), not from the target table's own data, each page — see `docs/ARCHITECTURE.md`, "CBMS-side resume cursor". | — required |
| | `Source.CommandType` | `"StoredProcedure"` (default) or `"Text"`. | `"StoredProcedure"` |
| | `Target.ConnectionString` | This job's target database connection string. | — required |
| | `Target.Table` | Schema-qualified target table, e.g. `"dbo.BCB_NEW2"`. | — required |
| | `Target.Columns` | Ordered target column names. **Column 0 is always the key** — it must be the source query's first result column too, copied straight through with no transformation, and used for `@LastRowId` paging and the audit row's from/to range. | — required, at least one |
| | `BatchSize` | Max rows per page pulled from this job's source. | `5000` |
| | `CommandTimeoutSeconds` | SQL command timeout for both this job's source calls and its target bulk copy. | `120` |
| `Email` | `EnableOnFailure` | Send one aggregate failure email per run if any job(s) failed (lists every failed job, not one email per job). | `true` |
| | `SmtpHost` / `SmtpPort` / `UseSsl` | SMTP relay settings. | — |
| | `SmtpUsername` / `SmtpPassword` | Only needed if the relay requires auth (most internal relays don't). | — |
| | `From` / `To` | Sender and recipient list. | — |
| `Serilog` | — | Standard Serilog config section (console + rolling file sinks). | see `appsettings.json` |

**Field-count check, not type-check**: before a job's first page of paging, the engine
compares the source query's result column count to `Target.Columns.Count` — a
mismatch fails that job immediately with a clear error and no partial work. Column
*types* aren't pre-validated; a type mismatch (e.g. a string too long for its target
column) surfaces as an ordinary bulk-copy error at insert time.

**Job isolation**: jobs run sequentially, in the order listed, under one process-level
lock. One job failing (source unreachable, insert error, field-count mismatch) is
recorded as `Failed` in `SyncRunHistory` and does **not** stop the other jobs in the
same run.

Config sources, in override order: `appsettings.json` → `appsettings.{Environment}.json`
→ environment variables → command-line args (standard `Host.CreateDefaultBuilder`
behavior). Set `DOTNET_ENVIRONMENT` to select an environment file. See
`appsettings.Development.json.example` for the pattern (copy to
`appsettings.Development.json`, fill in real connection strings, never commit it).

Options are validated at startup (a custom `SyncOptionsValidator`, since
`Sync:Jobs` is a nested collection that `ValidateDataAnnotations()` can't recurse
into) — a missing required value on any job fails fast with a clear message and exit
code 1, before any connection is attempted.

Connection strings are stored as plaintext in `appsettings.json`. Manage access to
that file (and any secrets management around it) separately from this app.

## How to add a new sync job

1. **Write the source stored procedure** on the job's source database. It must:
   - Accept `@LastRowId BIGINT, @BatchSize INT`.
   - Return at most `@BatchSize` rows, ordered by the key column ascending, and
     **never return a row it has already returned in a previous call** — dedup/resume
     tracking is entirely this proc's responsibility (see
     `sql/02_usp_GetBCBNewData_CCRISB2B.sql` for a worked example: a separate
     `CbmsB2BLink_SentLog` tracking table, mark-on-read, no changes to the business
     tables it reads from).
   - Return its result columns in the exact order you'll list in `Target.Columns`,
     with the key as column 0.
2. **Confirm the target table** exists with the columns you'll list in
   `Target.Columns` (in that order). No SQL Server table type or TVP is needed — the
   engine bulk-copies positionally.
3. **Add a job entry** to `Sync:Jobs` in `appsettings.json`:
   ```json
   {
     "JobKey": "MyNewJob",
     "Source": {
       "ConnectionString": "Server=...;Database=...;...",
       "CommandText": "usp_MyNewJobSource",
       "CommandType": "StoredProcedure"
     },
     "Target": {
       "ConnectionString": "Server=...;Database=...;...",
       "Table": "dbo.MyTargetTable",
       "Columns": ["KeyColumn", "Field2", "Field3"]
     },
     "BatchSize": 5000,
     "CommandTimeoutSeconds": 120
   }
   ```
4. **Run it once locally** (`dotnet run --project src/CBMSB2BLink.Console`) and check
   the log line: `Sync succeeded for MyNewJob: N records, RowId X-Y, Nms`. A
   field-count mismatch shows up immediately as a clear `Failed` error naming both
   counts — fix `Target.Columns` or the proc's `SELECT` list to match.
5. **Run it again** — it should log `No new records for MyNewJob.` if the source
   proc's own dedup is working (nothing new to return).
6. `dbo.SyncRunHistory` is auto-created in the job's target database the first time it
   runs — no manual script needed for that table. If the target table itself doesn't
   exist yet, create it first (that part isn't automated).

## CBMSB2BLink.Monitoring.Api settings

Own `appsettings.json`, hosted separately, read-only:

| Section | Key | Meaning | Default |
|---|---|---|---|
| `ConnectionStrings` | `Cbms` | The database to read `SyncRunHistory` from for the dashboard. Set this to whichever job's target database you want to monitor — if jobs use different target databases, point this at the one(s) whose `SyncRunHistory` you want visible here. | — required |
| `Dashboard` | `SyncKeys` | Which `JobKey`s to show in the sync-key selector. | `["BCB_NEW"]` if left empty |
| | `StalenessThresholdMinutes` | A sync key is "unhealthy" if its last run wasn't within this window (or failed). | `60` |

**No built-in authentication.** The dashboard is read-only and intended for the internal
network only. If it needs to be reachable from anywhere less trusted than that, put it
behind IIS Windows/Basic auth, an internal reverse proxy with auth, or a network ACL —
none of that is built into the app itself. Don't expose it directly to the internet.

## Hosting the monitoring dashboard

Standard ASP.NET Core app — same publish/host options as any other:

```powershell
dotnet publish src/CBMSB2BLink.Monitoring.Api -c Release -o C:\Apps\CBMSB2BLinkMonitoring
```

Simplest option: run as a Windows Service via `sc create` pointing at the published
`.exe` (Kestrel self-hosts, no IIS required), or host behind IIS with the ASP.NET Core
Module if that's already the standard in your environment. Set the listening port via
`ASPNETCORE_URLS` (environment variable) or `appsettings.json`'s `Kestrel` section.
Unlike `CBMSB2BLink.Console`, this is a long-running process, not a scheduled one-shot.

## Task Scheduler setup

1. Publish: `dotnet publish src/CBMSB2BLink.Console -c Release -r win-x64 --self-contained false -o C:\Apps\CBMSB2BLink`
2. Create a Basic Task in Task Scheduler:
   - **Action**: Start a program → `C:\Apps\CBMSB2BLink\CBMSB2BLink.exe`
   - **Start in**: `C:\Apps\CBMSB2BLink`
   - **Trigger**: e.g. every 15 minutes (match to how fresh the target data needs to be
     — one run processes every configured job, so pick a cadence that suits the
     job with the tightest freshness requirement)
   - **Run whether user is logged on or not** — required for unattended execution.
   - **Settings → Do not start a new instance** — primary defense against overlapping
     runs; the app's own file lock is the backstop.
   - **Settings → Stop the task if it runs longer than** — set a bit above
     `Sync:MaxRunDurationSeconds` as an outer safety net.
3. Verify the run: check exit code (0 = success incl. no-new-data on every job, 1 = at
   least one job failed) and the log file under `C:\ProgramData\CBMSB2BLink\logs\`.

Alternates noted in the original spec (SQL Agent CmdExec job, a Windows Service with an
internal timer) both work with this same executable — CmdExec would call the same
published `.exe`; a Windows Service wrapper is unnecessary complexity for something this
short-lived and is not recommended unless there's already service infrastructure to hang
it off of.
