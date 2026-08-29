# CBMSB2BLink — Architecture

## Purpose

CBMSB2BLink is a scheduled .NET 6 Windows console app that runs a configured list of
independent sync jobs (`Sync:Jobs`), each copying new rows from its own source
database/query into its own target table, **one page at a time, each page committed
on its own** — not one big batch collected in memory and committed once at the end
of the run. Before every single page pull, CBMSB2BLink re-reads that job's resume
position from `dbo.SyncRunHistory` — `MAX(SourceRowIdTo)` for that `JobKey`, an
app-owned audit table, deliberately **not** read from the target business table's own
data (see "CBMS-side resume cursor" below for why) — to seed that page's
`@LastRowId`. `@LastRowId` is the **only** dedup mechanism in the whole pipeline — the
source query (`usp_GetBCBNewData`) has no sent-log or mark-on-read tracking of its
own, so correctness depends entirely on `@LastRowId` strictly increasing, and on
`SyncRunHistory` accurately reflecting what actually got committed. CBMSB2BLink
writes exactly that audit trail (`SyncRunHistory`, in that job's own target database,
**one row per committed page**, not one row per run, written in the same transaction
as the page's insert) — that table is simultaneously the audit log *and* the resume
cursor's source of truth, not something separate the app reads back on top of. One
job failing does not stop the others in the same run (see "Multi-job orchestration"
below).

```
                ┌───────────────────────┐
                │   CBMSB2BLink.exe      │
                │  (scheduled, one-shot) │
                └──────┬──────────┬──────┘
                       │          │
                 READ  │          │ WRITE (transactional, per page)
                       ▼          ▼
              each job's Source    each job's Target
              (proc/SQL + params)  (table, SyncRunHistory)
```

Today's one configured job (`BCB_NEW2`) reads `usp_GetBCBNewData` against CCRISB2B
(`src_tblRetRpt`/`src_tblCRARawReport`) and writes `dbo.BCB_NEW2` in CBMS — unchanged
from the pipeline described in
`docs/superpowers/specs/2026-08-23-bcb-new2-pipeline-design.md`, now expressed as one
entry in `Sync:Jobs` instead of hardcoded.

### Multi-job orchestration

`SyncEngine.RunAsync` acquires one process-level file lock covering the whole run,
then runs every configured job in `Sync:Jobs`, in order. Each job is isolated: if one
job's source is unreachable, its insert fails, or its source query's column count
doesn't match its configured `Target.Columns`, that job is recorded as `Failed` and
the run moves on to the next job rather than aborting. After all jobs finish, one
aggregate failure email lists every job that failed in that run (not one email per
job). The process exits non-zero if any job failed.

## Components (built)

| Project | Responsibility |
|---|---|
| `CBMSB2BLink.Core` | Domain models, options (including `SyncJobOptions`), repository/notification interfaces, `SyncEngine` multi-job orchestration — no I/O. |
| `CBMSB2BLink.Data` | SQL Server implementations (Dapper + `Microsoft.Data.SqlClient`, `SqlBulkCopy`) of the Core interfaces. |
| `CBMSB2BLink.Console` | Composition root: Generic Host, config binding + validation, Serilog, email, file-based run lock. |
| `CBMSB2BLink.Monitoring.Api` | Read-only monitoring dashboard/API over CBMS `SyncRunHistory`. |
| `CBMSB2BLink.Tests` | `SyncEngine` multi-job orchestration tests, plus `HealthCalculator` tests, against mocked dependencies. |

## Tech stack

| Layer | Choice | Where |
|---|---|---|
| Runtime | .NET 6 (`net6.0`), C# | all 4 projects |
| Host | `Microsoft.Extensions.Hosting` Generic Host (console) / ASP.NET Core Minimal API (`Monitoring.Api`) | `Console/Program.cs`, `Monitoring.Api/Program.cs` |
| Data access | Dapper 2.1.35 (queries + stored-proc calls) + raw ADO.NET (`Microsoft.Data.SqlClient` 5.2.2, `SqlBulkCopy`) for the destination insert | `CBMSB2BLink.Data` |
| Database | SQL Server — each job has its own source and target connection strings; today's one configured job uses two, `CCRISB2B` (source, read-only) and `CBMS` (destination, read/write) | — |
| Logging | Serilog, console + rolling daily file sink (`Serilog.Sinks.File`), 30-day retention, enriched with machine name | `Console/appsettings.json` |
| Email | MailKit 4.17.0 (SMTP), failure notifications only | `EmailNotificationService.cs` |
| Scheduling | Windows Task Scheduler (primary); SQL Agent CmdExec job or a Windows Service timer are documented alternates, not built | `RUNBOOK.md` |
| Tests | xUnit, `Moq` for interface mocks, ASP.NET's own `HttpMessageHandler` fake for HTTP tests | `CBMSB2BLink.Tests` |
| Concurrency guard | Exclusive OS file lock (`FileStream` with `FileShare.None`), not a DB lock | `Console/Infrastructure/FileRunLock.cs` |

No ORM beyond Dapper's micro-mapping, no message queue, no caching layer, no
containerization — this is deliberately a small, single-purpose sync utility, not a
service platform.

## Run flow (step by step)

1. **Acquire an exclusive file lock** (`%ProgramData%\CBMSB2BLink\run.lock` by
   default, via a raw `FileStream` opened with `FileShare.None` — see
   `FileRunLock.cs`) — defense in depth against two scheduled runs overlapping and
   racing each other's inserts, on top of Task Scheduler's own "do not start a new
   instance" setting. If the lock is already held, the run exits immediately with a
   `Failed`/skipped result — it does **not** wait or retry.
2. **For each page, pull then immediately commit — repeat until done**
   (`SyncEngine.RunJobAsync`'s loop). Every iteration:
   a. Re-read the resume cursor — `IResumeCursorRepository.GetLastRowIdAsync`
      (`SELECT MAX(SourceRowIdTo) FROM dbo.SyncRunHistory WHERE SyncKey = @JobKey AND
      Status = 'Success'`) against the **target** database — fresh, not cached from a
      previous iteration or a previous run.
   b. `SqlSourceRepository` opens a `SqlConnection` to CCRISB2B and calls
      `EXEC usp_GetBCBNewData @LastRowId, @BatchSize` with that cursor.
   c. If the page is empty and this is the *first* page this run: record a
      `NoNewData` row in `SyncRunHistory` and stop — no CBMS write.
   d. If the page is empty but earlier pages already committed this run: stop, the
      job is done (see step 4).
   e. Otherwise, **one CBMS transaction** (`TargetUnitOfWork`, a single
      `SqlConnection` + `SqlTransaction`, default SQL Server isolation level — READ
      COMMITTED) does both of the following for *this page only*, and only commits
      if both succeed:
      - **Insert**: this page's `DataTable` is bulk-copied into the target table
        positionally via `SqlBulkCopy` (`SqlDestinationRepository.InsertBatchAsync`)
        — no SQL Server table type or TVP needed.
      - **Append audit row**: `INSERT INTO dbo.SyncRunHistory (...)` for *this
        page* (recording the `ROWID`/`CMS_NO` range just inserted) — same
        transaction.
      - `COMMIT`. Because both are one transaction, a crash between them rolls
        back only this page — every earlier page in this run is unaffected,
        already committed and already reflected in `SyncRunHistory`'s
        `MAX(SourceRowIdTo)` for the *next* iteration's cursor (see "Failure &
        recovery scenarios").
   f. Loop back to (a) unless: the page came back **smaller** than `BatchSize`
      (nothing left), the run has now pulled `BatchAllowedMaxRecord` rows total
      (see "Capacity & limits"), or `MaxRunDurationSeconds` is exceeded.
3. Once the loop ends, `RunAsync` returns one **aggregate** `SyncRunResult` per job
   (total rows read/inserted, overall `RowID` range, total duration) — this is what
   drives the failure email and the process exit code. It is not itself written to
   `SyncRunHistory`; the per-page rows written in step 2e are the durable record.
4. **On any failure** (source unreachable, destination unreachable, insert error,
   timeout): roll back that page's CBMS transaction only, best-effort record a
   `Failed` `SyncRunHistory` row on its **own separate connection** (outside the
   failed transaction, so the audit row survives even though the page's transaction
   didn't commit), send a failure email via MailKit, and exit non-zero so Task
   Scheduler / SQL Agent can flag the run. Every page committed *before* the failure
   stays committed.

### CBMS-side resume cursor — from SyncRunHistory, never from the target table's own data

This app has gone through four different designs for "where does a run resume from":

1. **`dbo.SyncControl`** (earliest) — a CBMS table CBMSB2BLink read at the start of
   each run and advanced on success. Removed in favor of keeping resume/dedup logic
   entirely on the CCRISB2B side.
2. **`dbo.CbmsB2BLink_SentLog`** (CCRISB2B, mark-on-read) — `usp_GetBCBNewData`
   tracked "already sent" itself via `NOT EXISTS`. Later dropped when the proc was
   rewritten to push `@LastRowId` filtering into its own `WHERE` clauses instead.
3. **`dbo.CbmsB2BLink_ResumeCursor`** (CBMS, briefly reintroduced) — a table
   tracking each job's `LastRowId`, auto-advanced on success, with ops able to
   `UPDATE` it by hand. This was **removed again** after it caused a real
   duplicate-key insert: the table's tracked value drifted out of sync with what was
   actually in the target table (e.g. the target got reset/reseeded independently of
   the cursor table), so the app started paging from a `@LastRowId` lower than data
   that already existed in the target, and tried to re-insert it.
4. **`MAX(Target.Columns[0]) FROM Target.Table`** (briefly, immediately after #3) —
   compute the cursor live from the target's own key column instead of tracking it
   anywhere separately, on the theory that whatever's actually in the target *is* the
   cursor, so nothing can drift. This assumption broke on `dbo.BCB_RSP_CRDCR`: its
   `Target.Columns[0]` (`CRDCR_RSP_CMS_NO`) turned out to be a server-generated
   `IDENTITY` column in the real BAU table — `SqlBulkCopy` silently discards whatever
   value the source actually sent for it and lets SQL Server auto-assign
   `1, 2, 3, ...` instead. So `MAX(CRDCR_RSP_CMS_NO)` reflected "how many rows have
   ever been inserted," not "the highest source `RowID` synced" — the seeded cursor
   stayed far below the source's real progress forever, and the same source rows got
   silently **re-inserted as duplicates** (no error, since the identity column always
   accepts a new value) on every subsequent page.

**Current design**: `IResumeCursorRepository.GetLastRowIdAsync` computes the seed
cursor fresh, every page, from `dbo.SyncRunHistory` — never from the target business
table:

```sql
SELECT MAX(SourceRowIdTo) FROM dbo.SyncRunHistory WHERE SyncKey = @JobKey AND Status = 'Success';
```

(`SqlResumeCursorRepository.GetLastRowIdAsync`, called from `SyncEngine.RunJobAsync`
with `job.JobKey`.) `SyncRunHistory` is an app-owned table — CBMSB2BLink creates and
writes it itself, it is never a BAU source/target table, and its
`SourceRowIdTo` column is written directly from the source's own returned `RowID`
(`page.Rows[^1][0]`), completely independent of whatever the target table's
`SqlBulkCopy` mapping does to its own columns. Because each page's `SyncRunHistory`
row is written in the **same transaction** as that page's target insert (see "Run
flow" step 2e above), the two can never drift apart the way design #3's separate
table could — there's no separate advance step to forget, and no dependency on the
target's key column meaning what the app assumes it means. This works identically
for every job regardless of what the target's own schema looks like, without
requiring any change to that (BAU-owned) schema.

**Accepted risk** (unchanged across all four designs, just relocated each time): if a
row's eligibility can flip from "not yet ready" to "ready" *after* a higher `RowID`
has already been synced (e.g. a status column populated asynchronously post-insert),
that lower-`RowID` row is skipped **forever** once `@LastRowId` passes it — nothing in
the current `usp_GetBCBNewData` re-checks rows below the cursor. This is only safe if
the source query's eligibility is monotonic with `RowID` (once a `RowID` has been
passed, nothing below it can later become newly eligible). This risk is inherent to
using `@LastRowId` as the *only* dedup mechanism (see the top of this doc) — it exists
regardless of where the cursor value comes from.

## Where the data lives during a run — is it bulk-loaded into memory?

Only **one page's worth at a time** — there is **no staging table, no temp file, no
queue**, and (unlike earlier revisions of this app) no whole-run in-memory
accumulation either:

- One page comes back from CCRISB2B as an ADO.NET `DataTable`
  (`SqlSourceRepository.GetNewRecordsAsync`).
- That same `DataTable` is handed straight to
  `SqlDestinationRepository.InsertBatchAsync` and streamed to SQL Server via
  `SqlBulkCopy`, positionally.
- Once that page's transaction commits, the `DataTable` goes out of scope and that
  memory is released — before the *next* page is even pulled. A job with a
  1,000,000-row backlog and `BatchSize: 5000` never holds more than ~5,000 rows in
  memory at once, not 1,000,000.
- The only durable record of "what happened" is `SyncRunHistory` (one row per
  committed page: row counts, `ROWID`/`BCB_CMS_No` range, duration) — not the row
  data itself, and not a resume position (see "CBMS-side resume cursor" above for
  where that actually comes from).

## Capacity & limits

What bounds a run — per job, since `BatchSize`/`BatchAllowedMaxRecord` live on
`SyncJobOptions`:

| Knob | Config | Default | Range | Effect |
|---|---|---|---|---|
| `Sync:Jobs[].BatchSize` | `SyncJobOptions.BatchSize` | 5,000 | 1–100,000 | Rows requested **per source call** (`TOP (@BatchSize)`, applied per branch in the current `usp_GetBCBNewData` — see its own notes on that). `SyncEngine.RunJobAsync`'s loop keeps calling (and committing) with this chunk size until a short/empty page comes back. |
| `Sync:Jobs[].BatchAllowedMaxRecord` | `SyncJobOptions.BatchAllowedMaxRecord` | 100,000 | ≥ `BatchSize`, ≤ 10,000,000 | Hard cap on total rows **committed** across all pages in one run, for that job. The loop stops as soon as this is reached, even if the last page was full and more data remains — the rest waits for the next call (resumed from `SyncRunHistory`'s `MAX(SourceRowIdTo)`, see "CBMS-side resume cursor" above). A safety valve independent of `MaxRunDurationSeconds`, so one run can't run indefinitely even if there's time left — note that unlike before, every page committed on the way to this cap is already durable, not held in memory pending a final commit. |
| `Sync:MaxRunDurationSeconds` | `SyncOptions.MaxRunDurationSeconds` | 1,800 (30 min) | 1–86,400 | A `CancellationTokenSource.CancelAfter(...)` set in `Program.cs` around the whole run (all jobs). If the paging + insert hasn't finished by then, the run is cancelled mid-flight — but only the *current, uncommitted* page is lost; every earlier page already committed. |
| `Sync:Jobs[].CommandTimeoutSeconds` | `SyncJobOptions.CommandTimeoutSeconds` | 120 | 1–3,600 | Per-SQL-command timeout (each proc call, each insert) — a network hiccup on one page fails that page's transaction rather than hanging indefinitely. |

So the real ceiling on "how much can one run copy" for a given job is
**`min(BatchAllowedMaxRecord, however many `BatchSize` pages fit inside the
remaining `MaxRunDurationSeconds`)`** — but unlike before, that ceiling only bounds
how much progress happens *per run*, not how much is held in memory at once (memory
usage is flat, one page at a time — see "Where the data lives" above). If a job's
backlog exceeds `BatchAllowedMaxRecord`, or a run can't finish an unusually large
backlog inside `MaxRunDurationSeconds` and gets cancelled mid-flight (only the page
in flight when that happens is rolled back — see "Failure & recovery scenarios"
below), the next run resumes from wherever `SyncRunHistory`'s `MAX(SourceRowIdTo)`
now stands (see "CBMS-side resume cursor" above) — so a large backlog drains over
multiple scheduled cycles either way.

**Not yet re-measured under this pipeline**: an earlier version of this app (the
retired `tblRPT`/`BCB_NEW` pipeline) was measured syncing a 500,000-row backlog
end-to-end in 17.5 seconds against a local SQL Server instance. That number no longer
applies to the current `src_tblRetRpt`/`BCB_NEW2` pipeline (different query shape) and
hasn't been re-measured — treat the batch-size/duration knobs above as having
comfortable headroom based on the old pipeline's numbers, not as a verified guarantee
for this one, until someone re-runs the same kind of test against
`sql/dev-seed-bigdata_CRARawReport_CCRISB2B_LocalTesting.sql`.

## Failure & recovery scenarios — what if the app doesn't run, or dies mid-run?

Since `usp_GetBCBNewData` dropped its own sent-log (`dbo.CbmsB2BLink_SentLog` no
longer exists) and there's no separate cursor table any more either, correctness now
depends entirely on `SyncRunHistory`'s `MAX(SourceRowIdTo)` only ever reflecting rows
that actually committed. The source query itself has no independent memory of what
it's already returned — re-querying the same `@LastRowId` range twice legitimately
returns the same rows twice.

**1. The process crashes / is killed / the run times out mid-execution.**
Each *page's* CBMS insert and its `SyncRunHistory` row commit in **one transaction**
(step 2e above), so a crash at any point before that page's `COMMIT` rolls back only
that page — every earlier page in the same run already committed and stays
committed. CBMS never ends up with a partially-inserted *page*, and `SyncRunHistory`'s
`MAX(SourceRowIdTo)` (the cursor) reflects exactly what actually committed — there's
nothing separate to roll back or leave stale, since the cursor is read from the exact
same table, and the exact same row, that recorded the page's success. Because nothing
on the CCRISB2B side marks rows "sent" independently of a page's commit, the *next*
call (whether later in the same run or a fresh run) reads the *same* unadvanced
cursor for whatever page never finished, and naturally re-pulls and retries those
exact rows — there is no permanent-skip risk from a mid-run crash the way there was
under the old mark-on-read `SentLog` design. The cursor's only real risk is the one
described in "CBMS-side resume cursor" above: a *successful* page advancing past a
`RowID` whose eligibility hadn't kicked in yet.

**2. The scheduled task itself doesn't run for a day, two days, or longer**
(host was down, Task Scheduler disabled, etc.) — this is the "data from
yesterday / -2 days" case. `SyncRunHistory`'s `MAX(SourceRowIdTo)` still reflects
wherever the last successful run left off, so the next run resumes from there and
pages through the accumulated backlog (see "Capacity & limits" above) — draining the
rest over subsequent runs if it doesn't all fit inside `MaxRunDurationSeconds`.
Whether `usp_GetBCBNewData`'s eligibility rules still include everything from that
far back is a separate question governed by the proc's own logic, not something
CBMSB2BLink controls.

## Why `SqlBulkCopy` instead of a table-valued parameter

Earlier revisions of this app used a hand-written SQL Server table type
(`dbo.BcbRecordTableType`) matched exactly to one hardcoded destination table's
columns, so `INSERT ... OUTPUT INSERTED.CMS_NO SELECT ... FROM @Records` could do the
insert and report back a generated identity range in one round trip. That doesn't work
once the destination table (and its column list) is config, not code — there's no
per-job SQL Server type to write by hand. `SqlBulkCopy` needs no such type: it maps
the in-memory `DataTable`'s columns to `Target.Columns` positionally at runtime. The
key range it reports back is computed from the `DataTable` itself before the insert
even runs (the key is always the source's first column, copied straight through, never
a target-generated identity) — see
`docs/superpowers/specs/2026-08-24-generic-sync-engine-design.md`, "Why no
OUTPUT-based identity capture".

---

## Monitoring dashboard / API (built)

**`CBMSB2BLink.Monitoring.Api`** — read-only ASP.NET Core minimal API + static
dashboard over CBMS `dbo.SyncRunHistory`. No new tables — the same one `SyncEngine`
already writes, and the only durable state CBMSB2BLink keeps.

- `GET /api/status?syncKey=BCB_NEW2` → the **most recent `SyncRunHistory` row's**
  `SourceRowIdTo`/`CmsNoTo` (reported as `lastRowId`/`lastCmsNo` — the highest
  `ROWID`/`CMS_NO` that run actually synced, not a live watermark; both are `null`
  if no run has happened yet for that key) plus a computed `isHealthy`. `SyncRunHistory`
  is the only source of truth for status — there's no separate control table to
  disagree with it. `NoNewData` counts as healthy (nothing to sync isn't broken);
  only `Failed`, or no run within `Dashboard:StalenessThresholdMinutes`, counts as
  unhealthy. Logic lives in `HealthCalculator` (pure functions, unit tested).
- `GET /api/runs?syncKey=BCB_NEW2&take=50` → recent `SyncRunHistory` rows.
- `GET /` → static dashboard (`wwwroot/index.html`, vanilla JS + inline CSS, no CDN
  dependency — deliberate, since bank intranets often block external resources).
- `GET /healthz` → liveness of the dashboard app itself.

**No built-in authentication** — it's read-only and assumed to sit on the internal
network only. If it needs to be reachable more broadly, put IIS-level auth or a network
ACL in front of it; this wasn't built into the app itself. See `CONFIGURATION.md`.

Queries run through `SyncStatusReader` (Dapper, its own CBMS connection) — deliberately
not through `CBMSB2BLink.Data`'s write-side repositories, since dashboard reads are a
different shape than what `ISyncRunHistoryRepository` exposes for the sync engine.

## HTTP fallback bridge (removed)

An earlier revision had `CBMSB2BLink.FallbackBridge.Api` — a small API hosted near
CCRISB2B that wrapped `usp_GetBCBNewData` over HTTP for when direct SQL to CCRISB2B
wasn't reachable, with `HttpSourceRepository : ISourceRepository` as its client side
and a `Sync:SourceMode=Http` switch to select it. Both were **removed** when the sync
engine generalized to multi-job (`Sync:Jobs`, see
`docs/superpowers/specs/2026-08-24-generic-sync-engine-design.md`, decision 11) —
`ISourceRepository`'s contract changed shape (`DataTable`, job-scoped connection
strings) and the fallback bridge depended on the old one. There is only one source
mode now: direct SQL, via `SqlSourceRepository`. Reintroducing an HTTP fallback would
be a reasonable follow-up, generalized for the job-based shape, but wasn't rebuilt.
