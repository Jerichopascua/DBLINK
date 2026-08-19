# CBMSB2BLink — Architecture

## Purpose

CBMSB2BLink is a scheduled .NET 6 Windows console app that copies new rows from
CCRISB2B (`tblRPT`) into CBMS (`BCB_NEW`), tracking progress with a watermark so each
run only pulls what's new since the last successful run.

```
                ┌───────────────────────┐
                │   CBMSB2BLink.exe      │
                │  (scheduled, one-shot) │
                └──────┬──────────┬──────┘
                       │          │
                 READ  │          │ WRITE (transactional)
                       ▼          ▼
                 CCRISB2B         CBMS
              usp_GetBCBNewData   BCB_NEW, SyncControl, SyncRunHistory
              (tblRPT)
```

## Components (built)

| Project | Responsibility |
|---|---|
| `CBMSB2BLink.Core` | Domain models, options, repository/notification interfaces, `SyncEngine` orchestration — no I/O. |
| `CBMSB2BLink.Data` | SQL Server implementations (Dapper + `Microsoft.Data.SqlClient`) of the Core interfaces. |
| `CBMSB2BLink.Console` | Composition root: Generic Host, config binding + validation, Serilog, DPAPI secret decryption, email, file-based run lock. |
| `CBMSB2BLink.Tests` | `SyncEngine` orchestration tests against mocked repositories. |

## Run flow

1. Acquire an exclusive file lock (`%ProgramData%\CBMSB2BLink\run.lock` by default) —
   defense in depth against an overlapping scheduled run corrupting the watermark, on
   top of Task Scheduler's own "do not start a new instance" setting.
2. Read the current watermark (`LastRowId`) from `dbo.SyncControl`.
3. Page through `usp_GetBCBNewData(@LastRowId, @BatchSize)` against CCRISB2B until a
   page returns fewer than `BatchSize` rows — bounds memory/timeout on large backlogs
   instead of pulling everything in one call.
4. If nothing new: record a `NoNewData` run and exit.
5. Otherwise, open one CBMS transaction and, within it: insert the batch into
   `BCB_NEW` via a table-valued parameter with `OUTPUT INSERTED.CMS_NO` (capturing the
   generated identity range), advance `SyncControl`, and append a `SyncRunHistory` row —
   then commit. Insert + watermark update + history are atomic: a crash mid-run leaves
   CBMS and the watermark exactly as they were, so a rerun safely reprocesses the same
   `ROWID` range. No dedup column is needed for this to be correct.
6. On any failure: roll back, best-effort record a `Failed` `SyncRunHistory` row (own
   connection, outside the failed transaction), send a failure email, and exit non-zero
   so Task Scheduler / SQL Agent can flag the run.

## Why a TVP instead of `SqlBulkCopy`

`SqlBulkCopy` is the usual choice for bulk inserts, but it doesn't reliably surface
server-generated identity values, and the watermark needs the exact `CMS_NO` range
inserted. A table-valued parameter (`dbo.BcbRecordTableType`) lets a single
`INSERT ... OUTPUT INSERTED.CMS_NO SELECT ... FROM @Records` statement do the insert and
return the generated identities in the same round trip, inside the same transaction as
the watermark update.

## Extension seam for the fallback bridge

`ISourceRepository` is the only thing `SyncEngine` depends on to read new records. The
shipped implementation (`SqlSourceRepository`) calls CCRISB2B directly. `SyncOptions.SourceMode`
(`"Sql"` today) is reserved to select an alternate implementation later without touching
`SyncEngine` — see "Phase 2" below.

---

## Phase 2 — not yet built

These were requested in the original spec but scoped out of this pass; they're
documented here so they can be picked up without re-deriving the design.

### Monitoring dashboard / API

`dbo.SyncRunHistory` and `dbo.SyncControl` (built in this pass) are the intended data
source. Recommended options, roughly in order of effort:

1. **Custom lightweight dashboard** — a small ASP.NET Core minimal API (`GET /api/status`,
   `GET /api/runs?take=50`) reading those two tables, plus a static HTML/JS page (e.g.
   Chart.js) showing: last successful sync time, current `LastRowId`/`LastCmsNo` lag,
   recent run history, and a red/green health indicator. Matches the spec's "small HTTP
   program running dashboard/API." Host it as its own Kestrel/IIS site; it does not need
   to run on the same schedule or machine as the sync app — it only reads.
2. **Existing tooling** — if Grafana (or similar) is already available in-house, point a
   SQL Server data source at `SyncRunHistory`/`SyncControl` instead of building a bespoke
   UI. Same tables, less code to maintain.

Either way, no new tables are needed beyond what's already in
`sql/01_CreateSyncControlAndRunHistory_CBMS.sql`.

### HTTP fallback bridge (source unreachable)

When direct SQL access to CCRISB2B isn't available (network segmentation, firewall,
maintenance window), the plan is a small HTTP API **hosted near CCRISB2B** that wraps
`usp_GetBCBNewData`:

```
                 (SQL unreachable)
CBMSB2BLink  ────────X────────►  CCRISB2B
     │                                ▲
     │ HTTP (fallback)                │ direct SQL
     ▼                                │
  Fallback Bridge API ────────────────┘
  (hosted on/near CCRISB2B network)
```

- Contract: `GET /api/bcb-new?lastRowId={n}&batchSize={n}` → JSON array of
  `{ rowId, idNo, createDate, amount }`, same semantics as the stored procedure
  (`ROWID > lastRowId`, ordered ascending, capped at `batchSize`).
- Auth: a shared API key (header, e.g. `X-Api-Key`), rotated via the same DPAPI-protected
  config pattern used for connection strings.
- Wiring: add an `HttpSourceRepository : ISourceRepository` in `CBMSB2BLink.Data` (or a
  new `CBMSB2BLink.Data.Http` project) that calls this endpoint instead of SQL. Select it
  in DI based on `SyncOptions.SourceMode == "Http"`, or automatically fall back to it
  after N consecutive SQL connection failures — either is a small change to
  `Program.cs`'s service registration; `SyncEngine` itself needs no changes, since it
  only depends on `ISourceRepository`.
- The CBMS side stays the write path in both modes — the bridge only replaces how new
  rows are *read*, not how they're inserted.
