# Generic Multi-Job Sync Engine Design

Status: approved by user (2026-08-24), ready for implementation planning.

## Summary

CBMSB2BLink is currently a single, hardcoded pipeline: one fixed source query shape
(`usp_GetBCBNewData` returning `BCB_CMS_No, BCB_IdNo1, ...`), one fixed destination
table (`BCB_NEW2`), typed end-to-end through `BcbRecord`. This is a **generalization**:
CBMSB2BLink becomes a config-driven runner for **multiple independent source→target
sync jobs**, each with its own source connection/query and target connection/table,
with no change to the underlying per-job contract that already exists
(`@LastRowId`/`@BatchSize` in, rows out, source SQL owns dedup — see
`docs/superpowers/specs/2026-08-23-bcb-new2-pipeline-design.md` and
`docs/ARCHITECTURE.md`, "No CBMS-side watermark").

`BCB_NEW2`'s current pipeline becomes **one entry** in the new `Sync:Jobs` config list,
using the same generic engine as any other job — not special-cased code.

## Decisions made during brainstorming

1. **Multiple jobs, one app.** A single run processes a configured list of jobs
   sequentially, not just one pipeline.
2. **Each job has its own source AND target connection strings** — jobs are not
   constrained to always be CCRISB2B→CBMS.
3. **Source SQL still owns pagination and dedup** (per the existing, deliberate
   architecture) — every job's source command must itself accept `@LastRowId`/
   `@BatchSize` and never return an already-sent row. CBMSB2BLink adds no watermark of
   its own, for any job.
4. **Field-count validation, not type validation.** Before paging starts, a job's
   source command's result column count is compared to its configured target column
   count. Mismatch fails that job immediately. Column *type* mismatches are not
   pre-validated — they surface as ordinary `SqlBulkCopy` errors at insert time, same
   as any ADO.NET type error today.
5. **Insert mechanism: `SqlBulkCopy` with a dynamically-built `DataTable`** (Approach A
   of three considered — see rejected alternatives below), not a per-job SQL Server
   table type and not a hand-built multi-row `INSERT`.
6. **One process-level lock covers the whole run** (all jobs), not one lock per job.
7. **Jobs are isolated at the top level**: one job failing is recorded as `Failed` for
   that job and does not stop the other jobs in the same run. Process exit code is
   non-zero if any job failed.
8. **`SyncRunHistory` lives in each job's own target database**, auto-created
   (`CREATE TABLE IF NOT EXISTS`, in code) the first time that job runs — not a shared
   control database. The audit row still commits in the same transaction as that job's
   `SqlBulkCopy` insert, preserving today's atomicity guarantee.
9. **`SyncRunHistory`'s column shape is unchanged** (`SyncKey`, `SourceRowIdFrom/To`,
   `CmsNoFrom/To`) even though, for this generic design, source-key and target-key
   values are *always* identical (no server-generated identity is ever captured
   anymore — see "Why no OUTPUT capture" below) — kept redundant on purpose so
   `CBMSB2BLink.Monitoring.Api`'s existing `SyncStatusReader` queries keep working
   completely unchanged. A job's `JobKey` is written into the existing `SyncKey`
   column — no new column for it.
10. **`CBMSB2BLink.Monitoring.Api` is untouched** by this change (true zero-change,
    per decision 9 — it only queries `SyncRunHistory` directly, no dependency on the
    interfaces below).
11. **HTTP-fallback source mode is dropped.** `src/CBMSB2BLink.FallbackBridge.Api` and
    `src/CBMSB2BLink.Data/HttpSourceRepository.cs` (plus their tests) are **deleted**,
    not stubbed — they depend on `ISourceRepository`'s old typed contract, which this
    change replaces. Git history preserves them if HTTP fallback is generalized later.
    `Sync:SourceMode` config option is removed (there is only one source mode now).

## Rejected alternatives for the insert mechanism

- **A generic reusable TVP** (e.g., a table type with `sql_variant` columns): rejected
  — `sql_variant` breaks `ORDER BY`, has awkward conversion rules, and no `MAX` types.
- **Dynamically-built multi-row `INSERT ... VALUES (...),(...)...`**: rejected — needs
  careful parameterization to stay injection-safe, hits SQL Server's ~2,100 parameter
  limit (capping effective batch size unpredictably), and is slower than bulk copy at
  volume (see the earlier `BCB_NEW2` pipeline's own performance note in
  `docs/ARCHITECTURE.md` about unindexed-source-query slowness — bulk insert speed
  matters more, not less, going forward).

## Why no `OUTPUT`-based identity capture

Today's insert uses `OUTPUT INSERTED.BCB_CMS_No` to report the range of keys just
inserted. In the generic design this is unnecessary: the key is always the *source's*
first column, copied positionally into the target's first configured column with no
transformation — never a target-side generated identity. The key range is therefore
already known from the in-memory `DataTable` before the insert even runs (`MIN`/`MAX`
of the first column), which is also why source-key and target-key are always identical
(decision 9).

## Configuration shape

```json
"Sync": {
  "MaxRunDurationSeconds": 1800,
  "LockFilePath": "",
  "Jobs": [
    {
      "JobKey": "BCB_NEW2",
      "Source": {
        "ConnectionString": "Server=...;Database=CCRISB2B;...",
        "CommandText": "usp_GetBCBNewData",
        "CommandType": "StoredProcedure"
      },
      "Target": {
        "ConnectionString": "Server=...;Database=CBMS;...",
        "Table": "dbo.BCB_NEW2",
        "Columns": [
          "BCB_CMS_No", "BCB_IdNo1", "BCB_IdNo2", "BCB_Name1", "BCB_DOB",
          "BCB_Nationality", "BCB_CreateDate", "BCB_LastUpdateBy", "BCB_ENTKEY",
          "BCB_RefNo", "BCB_SCR_Scored_TxnCode"
        ]
      },
      "BatchSize": 5000,
      "CommandTimeoutSeconds": 120
    }
  ]
}
```

- `MaxRunDurationSeconds` stays a **whole-run** budget across every job in the list
  (one `CancellationTokenSource.CancelAfter(...)` covering the full run), not reset
  per job — matches today's "one scheduled task, one time budget" operational model.
- `LockFilePath` stays global — one process-level lock.
- `CommandType` is `"StoredProcedure"` or `"Text"` (raw SQL), matching
  `System.Data.CommandType`.
- `Columns[0]` and the source command's first result column are always the key. The
  key's underlying value must be convertible to `Int64` (used for the `@LastRowId`
  parameter and for `MIN`/`MAX` range computation) — non-integer keys are out of scope
  for this design (YAGNI: nothing in the current jobs needs one).
- `ConnectionStringsOptions` (today's fixed `CcrisB2B`/`Cbms` top-level connection
  strings) is removed — every job carries its own.

## Interface/class changes

- **`BcbRecord`**: deleted. Rows flow as `System.Data.DataTable` end to end.
- **`ISourceRepository`**: method signature generalizes to take connection string,
  command text/type, `@LastRowId`, `@BatchSize`, and a command timeout, returning a
  `DataTable` for that page. No longer resolved via DI with a single fixed connection
  string — the orchestrator (see below) passes each job's own `Source` config in per
  call.
- **`SqlSourceRepository`**: sole implementation now (HTTP alternative removed).
  Builds its returned `DataTable` directly from the `SqlDataReader`'s schema
  (`DataTable.Load(reader)` or equivalent), so column types come from the actual
  source result set, not a hardcoded shape.
- **`IDestinationRepository`**: method signature generalizes to take the unit of work,
  target table name, ordered target column list, and the accumulated `DataTable`,
  returning an `InsertBatchResult`-shaped range (computed from the `DataTable`'s first
  column, per "Why no `OUTPUT`-based identity capture" above — not from a DB round
  trip).
- **`SqlDestinationRepository`**: implementation switches from
  TVP-`INSERT...OUTPUT...SELECT...FROM @Records` to `SqlBulkCopy` (constructed with
  the job's `SqlTransaction`, `DestinationTableName = Target.Table`, and
  `ColumnMappings` built from the source `DataTable`'s column ordinals to
  `Target.Columns` positionally).
- **`ICbmsUnitOfWork`/`CbmsUnitOfWork`/`ICbmsUnitOfWorkFactory`**: renamed to
  `ITargetUnitOfWork`/`TargetUnitOfWork`/`ITargetUnitOfWorkFactory`.
  `ITargetUnitOfWorkFactory.Create(string connectionString)` now takes the job's
  target connection string as a parameter instead of being wired to one fixed CBMS
  connection string at DI-registration time.
- **`ISyncRunHistoryRepository`/`SqlSyncRunHistoryRepository`**: same
  parameterize-by-connection-string generalization — targets whichever database the
  current job's unit of work is open against, not a fixed CBMS connection.
- **`dbo.BcbRecordTableType`** (SQL Server table type): dropped entirely — no longer
  needed once the insert mechanism is `SqlBulkCopy`. `sql/01_CreateSyncRunHistory_CBMS.sql`
  as a hand-run setup script is also retired: `SyncRunHistory`'s `CREATE TABLE IF NOT
  EXISTS` moves into C# code (executed once per job's target DB, at that job's first
  use in a run), consistent with "each target DB may be unfamiliar/new," rather than
  requiring a DBA to run a script per target DB up front.
- **`SyncOptions`**: today's flat single-pipeline shape (`SyncKey`,
  `StoredProcedureName`, `BatchSize`, `CommandTimeoutSeconds`, `SourceMode`,
  `LockFilePath`, `MaxRunDurationSeconds`) splits into a top-level `SyncOptions`
  (`MaxRunDurationSeconds`, `LockFilePath`, `Jobs: List<SyncJobOptions>`) and a new
  `SyncJobOptions` (`JobKey`, `Source: SourceJobOptions`, `Target: TargetJobOptions`,
  `BatchSize`, `CommandTimeoutSeconds`). `SourceMode` is removed (decision 11).

## Orchestration (`SyncEngine`)

`RunAsync` becomes, at a high level:

1. Acquire the one process-level file lock (unchanged from today — still exits
   immediately, no wait/retry, if already held).
2. Start the whole-run `CancellationTokenSource.CancelAfter(MaxRunDurationSeconds)`.
3. For each configured job, **in order, isolated**:
   - Run that job's existing pull-all-pages / bulk-insert / record-history flow
     (same shape as today's single-pipeline `RunAsync` body, now parameterized by
     that job's `SyncJobOptions`).
   - On success or `NoNewData`: record that job's result, continue to the next job.
   - On failure: catch, roll back that job's transaction, best-effort record a
     `Failed` `SyncRunHistory` row for that job (own connection, per today's
     pattern), continue to the next job — **do not abort the remaining jobs**.
4. After all jobs: send **one aggregate failure email** listing every job that
   failed in this run (not one email per failed job — avoids notification spam when
   several jobs fail together, e.g. a shared network blip). Exit code is non-zero if
   any job failed.

`SyncRunResult` (today: one result per run) becomes a per-job result; the overall
run produces a `List<SyncRunResult>` (or equivalent), one per configured job.

## Migrating `BCB_NEW2`

The existing `sql/02_usp_GetBCBNewData_CCRISB2B.sql` proc and `BCB_NEW2` table are
**unchanged** — they already match this design's contract exactly (paginated,
self-deduping source proc; a target table with a plain column list). Only the app-side
config changes: `BCB_NEW2`'s pipeline becomes the one entry shown in the "Configuration
shape" example above, using the columns already established in
`docs/superpowers/specs/2026-08-23-bcb-new2-pipeline-design.md`.

## Testing approach

- `SyncEngineTests`: reshape around the new per-job orchestration — happy path
  (multiple jobs succeed), one job fails while another succeeds (isolation), field-count
  mismatch fails a job before any paging happens, multi-page batching still works
  per job, `NoNewData` per job.
- New tests for the field-count validation logic and for `SqlBulkCopy` column-mapping
  construction (source `DataTable` schema → `Target.Columns`), independent of a live
  database where feasible (e.g., validation logic as a pure function over column
  counts/names).
- `HttpSourceRepositoryTests`/`FallbackBridge.Api` tests: deleted along with the code
  they test (decision 11).
- End-to-end: re-run the same kind of live verification used for the `BCB_NEW2`
  cutover (seed scratch data, run the console app twice, confirm dedup and correct
  row counts) — now via a `Jobs` config containing the single `BCB_NEW2` entry, to
  prove the generalized engine reproduces today's already-verified behavior before
  considering the generalization safe.

## Open items carried into implementation planning

- Exact `DataTable`→`SqlBulkCopy` column-mapping code shape (by ordinal vs. by name)
  — by ordinal is specified here (decision 5's "positionally"); confirm during
  implementation that `SqlBulkCopyColumnMapping` is built strictly by source-column
  index → `Target.Columns[index]`, not by matching names, since source and target
  column names are never assumed to match.
- Naming for the renamed unit-of-work abstraction (`ITargetUnitOfWork` proposed above)
  — confirm no other in-repo naming convention is preferred before implementation.
