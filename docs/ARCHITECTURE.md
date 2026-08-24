# CBMSB2BLink — Architecture

## Purpose

CBMSB2BLink is a scheduled .NET 6 Windows console app that runs a configured list of
independent sync jobs (`Sync:Jobs`), each copying new rows from its own source
database/query into its own target table. Each job keeps a resume watermark
(`dbo.CbmsB2BLink_ResumeCursor`, one row per `JobKey`) in its own target database —
read once at the start of the run to seed `@LastRowId` for the first page, then
advanced to the final cursor on every success (see "CBMS-side resume watermark"
below). `@LastRowId` is now the **only** dedup mechanism in the whole pipeline — the
source query (`usp_GetBCBNewData`) has no sent-log or mark-on-read tracking of its own
any more, so correctness depends entirely on `@LastRowId` strictly increasing, both
within a run's paging loop and across runs via the watermark. CBMSB2BLink also writes
an audit trail of what each job's run did (`SyncRunHistory`, in that job's own target
database) — that table stays history-only, not something the app reads back to decide
what to pull next. One job failing does not stop the others in the same run (see
"Multi-job orchestration" below).

```
                ┌───────────────────────┐
                │   CBMSB2BLink.exe      │
                │  (scheduled, one-shot) │
                └──────┬──────────┬──────┘
                       │          │
                 READ  │          │ WRITE (transactional, per job)
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
2. **Pull new rows from CCRISB2B, one page at a time** —
   `SyncEngine.RunJobAsync` first reads the job's watermark
   (`IResumeCursorRepository.GetLastRowIdAsync`, `dbo.CbmsB2BLink_ResumeCursor` in the
   **target** database) to seed `@LastRowId` for the first call only. Then
   `SqlSourceRepository` opens a `SqlConnection` to CCRISB2B and calls
   `EXEC usp_GetBCBNewData @LastRowId, @BatchSize`. `SyncEngine.PullAllPagesAsync`
   loops — each returned page is appended to one in-memory `DataTable`, and
   `@LastRowId` advances *within this run* to that page's last `RowID`, so the next
   call picks up where the last one left off — until a page comes back **smaller**
   than `BatchSize` (meaning there's nothing left) or the loop times out (see
   "Capacity & limits" below). `BatchSize` is the chunk size requested per call, not a
   cap on the run — a run keeps calling until it has drained everything available.
3. **If nothing new**: record a `NoNewData` row in `SyncRunHistory` and exit 0 —
   no CBMS write.
4. **Otherwise, one CBMS transaction** (`CbmsUnitOfWork`, a single `SqlConnection` +
   `SqlTransaction`, default SQL Server isolation level — READ COMMITTED) does both
   of the following, and only commits if both succeed:
   - **Insert**: the full in-memory batch is loaded into an ADO.NET `DataTable` and
     sent as one table-valued parameter (`dbo.BcbRecordTableType`) in a single
     `INSERT INTO dbo.BCB_NEW2 (BCB_CMS_No, BCB_IdNo1, ...) OUTPUT INSERTED.BCB_CMS_No
     SELECT ... FROM @Records` statement — one round trip for the whole batch. Unlike
     the retired `tblRPT`/`BCB_NEW` pipeline, `BCB_CMS_No` is not a server-generated
     identity — it's the source `RowID` copied straight through — so
     `OUTPUT INSERTED.BCB_CMS_No` here just reports the range of values that were
     inserted, for the audit row, rather than capturing generated identities
     (`SqlDestinationRepository.InsertBatchAsync`).
   - **Append audit row**: `INSERT INTO dbo.SyncRunHistory (...)` (recording the
     `ROWID`/`CMS_NO` ranges just inserted) — same transaction.
   - `COMMIT`. Because both are one transaction, a crash between them rolls
     everything back automatically (see "Failure & recovery scenarios").
5. **On any failure** (source unreachable, destination unreachable, insert error,
   timeout): roll back the CBMS transaction, best-effort record a `Failed`
   `SyncRunHistory` row on its **own separate connection** (outside the failed
   transaction, so the audit row survives even though the real transaction didn't
   commit), send a failure email via MailKit, and exit non-zero so Task Scheduler /
   SQL Agent can flag the run.

### CBMS-side resume watermark — the only dedup mechanism now, accepted risk

Earlier revisions of this app kept a `dbo.SyncControl` table in CBMS (`LastRowId`,
`LastCmsNo`) that CBMSB2BLink read at the start of each run and advanced on success.
That table was removed at one point in favor of keeping resume/dedup logic entirely on
the CCRISB2B side — for a while, `usp_GetBCBNewData` tracked "already sent" itself via
`NOT EXISTS` against `dbo.CbmsB2BLink_SentLog` (mark-on-read). A near-equivalent
watermark table has since been **reintroduced** in CBMS —
`dbo.CbmsB2BLink_ResumeCursor` (`sql/04_CreateCbmsB2BLinkResumeCursor_CBMS.sql`, one
row per `JobKey`) — and, separately, `usp_GetBCBNewData` was rewritten to drop
`CbmsB2BLink_SentLog` entirely (that table has been dropped from CCRISB2B). So today
**`@LastRowId`/`dbo.CbmsB2BLink_ResumeCursor` is the only thing preventing a row from
being sent twice** — there is no independent sent-log backstop any more.

- **Read**: `SyncEngine.RunJobAsync` reads the watermark via
  `IResumeCursorRepository.GetLastRowIdAsync` once at the start of the run, to seed
  `@LastRowId` for the *first* page only — `PullAllPagesAsync`'s loop then advances
  `@LastRowId` locally for subsequent pages within that same run (see step 2 above).
- **Auto-advance**: on every successful run, `IResumeCursorRepository.SetLastRowIdAsync`
  upserts the watermark to that run's `SourceRowIdTo` (the last page's last `RowID`),
  in the **same transaction** as the `BCB_NEW2` insert and the `SyncRunHistory` row —
  so it only advances when the insert actually committed.
- **Manual override**: ops can run `UPDATE dbo.CbmsB2BLink_ResumeCursor SET LastRowId =
  ... WHERE JobKey = ...` directly at any time to force a specific resume point (rewind
  to reprocess a range, or fast-forward past a known-bad one) — there is no separate
  override column or app-side mechanism; the app always just reads whatever value is
  currently in the table.

**Accepted risk**: this carries the exact failure mode the original `dbo.SyncControl`
watermark had, now with no `SentLog` safety net underneath it. If a row's eligibility
can flip from "not yet ready" to "ready" *after* a higher `RowID` has already been
synced (e.g. a status column populated asynchronously post-insert), that lower-`RowID`
row is skipped **forever** once `@LastRowId` passes it — nothing in the current
`usp_GetBCBNewData` re-checks rows below the cursor. This is only safe if the source
query's eligibility is monotonic with `RowID` (once a `RowID` has been passed, nothing
below it can later become newly eligible).

## Where the data lives during a run — is it bulk-loaded into memory?

Yes, entirely in memory, and only for the duration of one run — there is **no
staging table, no temp file, no queue**. The full chain:

- CCRISB2B rows come back as ADO.NET `DataTable` pages
  (`SyncEngine.PullAllPagesAsync` → `SqlSourceRepository.GetNewRecordsAsync`), and
  every page pulled during the run is merged into one in-memory `DataTable`
  (`DataTable.ImportRow`). Nothing is written to disk or handed off between pages.
- At insert time, that same `DataTable` is handed to
  `SqlDestinationRepository.InsertBatchAsync` and streamed to SQL Server as one
  table-valued parameter.
- Once the transaction commits (or the process exits), that memory is released —
  nothing about the batch persists on the app side. The only durable record of
  "what happened" is the `SyncRunHistory` row (row counts, `ROWID`/`BCB_CMS_No`
  ranges, duration) — not the row data itself, and not a resume position.

`BcbRecord` holds 12 mostly-short business fields (customer IDs, name, DOB, a few
short codes, one date) — memory pressure in practice is dominated by row *count*, not
row size — see limits below.

## Capacity & limits

What bounds a run — per job, since `BatchSize`/`BatchAllowedMaxRecord` live on
`SyncJobOptions`:

| Knob | Config | Default | Range | Effect |
|---|---|---|---|---|
| `Sync:Jobs[].BatchSize` | `SyncJobOptions.BatchSize` | 5,000 | 1–100,000 | Rows requested **per source call** (`TOP (@BatchSize)`, applied per branch in the current `usp_GetBCBNewData` — see its own notes on that). `SyncEngine.PullAllPagesAsync` keeps calling with this chunk size until a short/empty page comes back. |
| `Sync:Jobs[].BatchAllowedMaxRecord` | `SyncJobOptions.BatchAllowedMaxRecord` | 100,000 | ≥ `BatchSize`, ≤ 10,000,000 | Hard cap on total rows accumulated across all pages **in one run**, for that job. `PullAllPagesAsync` stops as soon as this is reached, even if the last page was full and more data remains — the rest waits for the next run (resumed via `dbo.CbmsB2BLink_ResumeCursor`). A safety valve independent of `MaxRunDurationSeconds`, so one run can't pull an unbounded backlog into memory even if there's time left. |
| `Sync:MaxRunDurationSeconds` | `SyncOptions.MaxRunDurationSeconds` | 1,800 (30 min) | 1–86,400 | A `CancellationTokenSource.CancelAfter(...)` set in `Program.cs` around the whole run (all jobs). If the paging + insert hasn't finished by then, the run is cancelled mid-flight. |
| `Sync:Jobs[].CommandTimeoutSeconds` | `SyncJobOptions.CommandTimeoutSeconds` | 120 | 1–3,600 | Per-SQL-command timeout (each proc call, each insert) — a network hiccup on one page fails the run rather than hanging indefinitely. |

So the real ceiling on "how much can one run copy" for a given job is
**`min(BatchAllowedMaxRecord, however many `BatchSize` pages fit inside the
remaining `MaxRunDurationSeconds`)`**, all held in memory at once. If a job's
backlog exceeds `BatchAllowedMaxRecord`, or a run can't finish an unusually large
backlog inside `MaxRunDurationSeconds` and gets cancelled mid-flight and rolled back
(see "Failure & recovery scenarios" below), the next run resumes from wherever
`dbo.CbmsB2BLink_ResumeCursor` last successfully advanced to (see "CBMS-side resume
watermark" above) — so a large backlog drains over multiple scheduled cycles either
way.

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
longer exists), correctness now depends entirely on `dbo.CbmsB2BLink_ResumeCursor`
only ever advancing when a run's CBMS write actually committed. The source query
itself has no independent memory of what it's already returned — re-querying the same
`@LastRowId` range twice legitimately returns the same rows twice.

**1. The process crashes / is killed / the run times out mid-execution.**
The CBMS insert, the `SyncRunHistory` row, and the `CbmsB2BLink_ResumeCursor` upsert
all commit in **one transaction** (step 4 above), so a crash at any point before
`COMMIT` rolls back all three together — CBMS ends up with **zero**
partially-inserted rows, and the watermark stays exactly where it was after the last
successful run. Because nothing on the CCRISB2B side marks rows "sent" independently
of that commit, the *next* run reads the *same* unadvanced watermark and naturally
re-pulls and retries the exact same rows — there is no permanent-skip risk from a
mid-run crash the way there was under the old mark-on-read `SentLog` design. The
watermark's only real risk is the one described in "CBMS-side resume watermark"
above: a *successful* run advancing past a `RowID` whose eligibility hadn't kicked in
yet.

**2. The scheduled task itself doesn't run for a day, two days, or longer**
(host was down, Task Scheduler disabled, etc.) — this is the "data from
yesterday / -2 days" case. `dbo.CbmsB2BLink_ResumeCursor` still holds wherever the
last successful run left off, so the next run resumes from there and pages through
the accumulated backlog (see "Capacity & limits" above) — draining the rest over
subsequent runs if it doesn't all fit inside `MaxRunDurationSeconds`. Whether
`usp_GetBCBNewData`'s eligibility rules still include everything from that far back is
a separate question governed by the proc's own logic, not something CBMSB2BLink
controls.

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
