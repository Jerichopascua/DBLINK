# CBMSB2BLink — Architecture

## Purpose

CBMSB2BLink is a scheduled .NET 6 Windows console app that runs a configured list of
independent sync jobs (`Sync:Jobs`), each copying new rows from its own source
database/query into its own target table. It keeps **no resume watermark of its own**
for any job — each job's source query is responsible for knowing what's already been
sent and excluding it. CBMSB2BLink only writes an audit trail of what each job's run
did (`SyncRunHistory`, in that job's own target database) — that table is history, not
something the app reads back to decide what to pull next. One job failing does not
stop the others in the same run (see "Multi-job orchestration" below).

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
2. **Pull new rows from CCRISB2B, one page at a time** — `SqlSourceRepository`
   opens a `SqlConnection` to CCRISB2B and calls `EXEC usp_GetBCBNewData
   @LastRowId, @BatchSize` (Dapper `QueryAsync`, `CommandType.StoredProcedure`).
   **`@LastRowId` starts at `0` on every run** — CBMSB2BLink has no cross-run
   watermark to seed it from. The proc itself is responsible for excluding rows it's
   already sent (see "No CBMS-side watermark" below); CBMSB2BLink just keeps calling
   it. `SyncEngine.PullAllPagesAsync` loops — each returned page is appended to one
   in-memory `List<BcbRecord>`, and `@LastRowId` advances *within this run only* to
   that page's last `ROWID`, purely so a multi-page pull doesn't re-request the same
   page — until a page comes back **smaller** than `BatchSize` (meaning there's
   nothing left) or the loop times out (see "Capacity & limits" below).
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

### No CBMS-side watermark — this is deliberate

Earlier revisions of this app kept a `dbo.SyncControl` table in CBMS (`LastRowId`,
`LastCmsNo`) that CBMSB2BLink read at the start of each run and advanced on success.
That table has been **removed**. The decision was to keep resume/dedup logic entirely
on the CCRISB2B side, inside `usp_GetBCBNewData` (or whatever replaces it) —
CBMSB2BLink is now a thin "call the proc, insert what it returns, log it" pipeline with
no opinion about what counts as "new." See "Failure & recovery scenarios" below for
what this means operationally, and `sql/02_usp_GetBCBNewData_CCRISB2B.sql`'s own
comments for the mark-on-read tracking pattern the proc implements via
`dbo.CbmsB2BLink_SentLog`.

## Where the data lives during a run — is it bulk-loaded into memory?

Yes, entirely in memory, and only for the duration of one run — there is **no
staging table, no temp file, no queue**. The full chain:

- CCRISB2B rows come back from Dapper as plain C# objects (`BcbRecordRow` →
  mapped to `BcbRecord`), held in a `List<BcbRecord>` that accumulates **every
  page** pulled during that run (`SyncEngine.PullAllPagesAsync`, `all.AddRange(page)`).
  Nothing is written to disk or handed off between pages.
- At insert time, that same list is copied into an in-memory ADO.NET `DataTable`
  (`SqlDestinationRepository.InsertBatchAsync`) and streamed to SQL Server as one
  table-valued parameter.
- Once the transaction commits (or the process exits), that memory is released —
  nothing about the batch persists on the app side. The only durable record of
  "what happened" is the `SyncRunHistory` row (row counts, `ROWID`/`BCB_CMS_No`
  ranges, duration) — not the row data itself, and not a resume position.

`BcbRecord` holds 12 mostly-short business fields (customer IDs, name, DOB, a few
short codes, one date) — memory pressure in practice is dominated by row *count*, not
row size — see limits below.

## Capacity & limits

There's no hardcoded cap on total records copied in one run. What actually bounds
a run:

| Knob | Config | Default | Range | Effect |
|---|---|---|---|---|
| `Sync:BatchSize` | `SyncOptions.BatchSize` | 5,000 | 1–100,000 | Rows requested per `usp_GetBCBNewData` call (`TOP (@BatchSize)`). The HTTP fallback bridge independently re-validates the same 1–100,000 range and returns `400` outside it. |
| `Sync:MaxRunDurationSeconds` | `SyncOptions.MaxRunDurationSeconds` | 1,800 (30 min) | 1–86,400 | A `CancellationTokenSource.CancelAfter(...)` set in `Program.cs` around the whole run. If paging + insert hasn't finished by then, the run is cancelled mid-flight. |
| `Sync:CommandTimeoutSeconds` | `SyncOptions.CommandTimeoutSeconds` | 120 | 1–3,600 | Per-SQL-command timeout (each proc call, each insert) — a network hiccup on one page fails the run rather than hanging indefinitely. |

So the real ceiling on "how much can one run copy" is **however many `BatchSize`
pages fit inside `MaxRunDurationSeconds`**, all held in memory at once. There's no
explicit "max total rows" setting; it's an emergent limit from batch size × time
budget. If you expect a very large first-ever backlog (e.g. syncing years of
history on day one), raise `BatchSize` (up to 100,000) and/or
`MaxRunDurationSeconds` for that run, or just let it run multiple scheduled
cycles — whether a later cycle picks up where an earlier one left off depends
entirely on the source SP's own tracking (see previous section), not on anything
CBMSB2BLink does.

**Not yet re-measured under this pipeline**: an earlier version of this app (the
retired `tblRPT`/`BCB_NEW` pipeline) was measured syncing a 500,000-row backlog
end-to-end in 17.5 seconds against a local SQL Server instance. That number no longer
applies to the current `src_tblRetRpt`/`BCB_NEW2` pipeline (different query shape, new
`CbmsB2BLink_SentLog` dedup join) and hasn't been re-measured — treat the batch-size/
duration knobs above as having comfortable headroom based on the old pipeline's
numbers, not as a verified guarantee for this one, until someone re-runs the same kind
of test against `sql/dev-seed-bigdata_CRARawReport_CCRISB2B_LocalTesting.sql`.

## Failure & recovery scenarios — what if the app doesn't run, or dies mid-run?

Correctness here now splits across a hard boundary: **CBMS-side atomicity is
guaranteed by this app; CCRISB2B-side "don't resend/don't lose" is entirely up to
the source stored procedure.** CBMSB2BLink cannot make guarantees about a system it
doesn't own the state of.

**1. The process crashes / is killed / the run times out mid-execution.**
The CBMS insert and the `SyncRunHistory` row commit in one transaction (step 4
above), so a crash at any point before `COMMIT` rolls back the entire
transaction — CBMS ends up with **zero** partially-inserted rows for that run.
That half is unconditionally safe, regardless of how the source proc works.

  What happens to the rows CBMSB2BLink had already *read* from CCRISB2B before the
  crash depends entirely on how the proc tracks "sent":
  - If the proc marks rows sent only *after* confirming CBMSB2BLink successfully
    processed them (a two-phase/ack pattern), those rows are naturally retried on
    the next run — same safety as the old watermark design.
  - If the proc marks rows sent **the moment they're read** (mark-on-read — the
    simplest pattern, and what the local test scaffolding in
    `sql/dev-seed_CCRISB2B_LocalTesting.sql` uses) — a crash *after* the source
    read but *before* the CBMS commit means CCRISB2B now believes those rows were
    sent, while CBMS never received them. **Those rows are permanently skipped**
    unless something reconciles the two sides after the fact. This is a real
    trade-off, not a bug — it buys simplicity on the source side at the cost of
    losing at-least-once delivery across a destination failure. Worth deciding
    deliberately when designing the real `usp_GetBCBNewData`, not defaulting into
    it.

**2. The scheduled task itself doesn't run for a day, two days, or longer**
(host was down, Task Scheduler disabled, etc.) — this is the "data from
yesterday / -2 days" case. Whether that backlog gets picked up cleanly on the
next run is now a question for the source SP's own logic, not something
CBMSB2BLink controls: CBMSB2BLink just keeps calling `usp_GetBCBNewData` with
`@LastRowId = 0` until a page comes back short. If the proc's "unsent" query
naturally includes everything that accumulated regardless of age, the backlog
syncs in one run (paged, per "Capacity & limits" above) exactly as it did under
the old watermark design. If the proc's tracking has its own staleness/retention
assumptions, those now govern what actually gets caught up.

**Caveat worth knowing operationally**: "make progress" is still all-or-nothing
*per run* on the CBMS side — `PullAllPagesAsync` accumulates the whole batch the
proc hands back into memory before a single row is written to CBMS, and the CBMS
write is one transaction. A backlog large enough to blow `MaxRunDurationSeconds`
produces **zero** inserted rows that cycle. Combined with a mark-on-read source
proc, a run that times out mid-pull is worse than before: rows already read (and
already marked sent by the proc) are now gone from both sides, not just delayed.
If mark-on-read is the chosen pattern, make sure `MaxRunDurationSeconds` and
`BatchSize` are sized with real headroom over expected backlog volume.

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
