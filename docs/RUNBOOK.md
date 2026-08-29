# CBMSB2BLink — Runbook

> For local end-to-end testing against scratch databases (no real CCRISB2B/CBMS
> access needed), see [TESTING.md](TESTING.md).

## Manual run

```powershell
cd C:\Apps\CBMSB2BLink
.\CBMSB2BLink.exe
echo $LASTEXITCODE   # 0 = success or no-new-data, 1 = failure
```

Or from source: `dotnet run --project src/CBMSB2BLink.Console`.

## Checking status

CBMSB2BLink keeps no watermark table — `SyncRunHistory` is the only status source:

```sql
-- Most recent run for this key (its SourceRowIdTo/CmsNoTo is the "how far did we get" indicator)
SELECT TOP 1 * FROM dbo.SyncRunHistory WHERE SyncKey = 'BCB_NEW' ORDER BY RunId DESC;

-- Recent runs
SELECT TOP 20 * FROM dbo.SyncRunHistory
WHERE SyncKey = 'BCB_NEW'
ORDER BY StartedUtc DESC;

-- Failures in the last 24h
SELECT * FROM dbo.SyncRunHistory
WHERE SyncKey = 'BCB_NEW' AND Status = 'Failed' AND StartedUtc > DATEADD(HOUR, -24, SYSUTCDATETIME())
ORDER BY StartedUtc DESC;
```

Also check `C:\ProgramData\CBMSB2BLink\logs\log-YYYYMMDD.txt` for the full exception
detail behind any `Failed` row (`SyncRunHistory.ErrorMessage` also has it, but the log
has surrounding context).

## Re-syncing a gap in CBMS

CBMSB2BLink computes each run's starting `@LastRowId` by reading
`MAX(SourceRowIdTo)` from `dbo.SyncRunHistory` for that job's `JobKey` (`Status =
'Success'` rows only) — **not** from the target business table's own data (see
`ARCHITECTURE.md`, "CBMS-side resume cursor", for why: a BAU target's key column can
be a server-generated `IDENTITY` unrelated to the source `RowID`, so reading the
target directly isn't reliable in general). That means re-syncing a gap means
adjusting `SyncRunHistory`, not the target table:

- **Gap at the tail** (the most recent rows are missing): delete the `SyncRunHistory`
  rows for that job whose `SourceRowIdTo` is at or above where the gap starts (or, to
  be surgical, `UPDATE` the relevant row's `SourceRowIdTo` down instead of deleting
  it). The next run's `MAX(SourceRowIdTo)` drops accordingly, and
  `usp_GetBCBNewData` will return everything above that again — assuming its own
  eligibility window (date range, status filter, etc.) still covers those `RowID`s.
  Check that before assuming a re-sync will actually surface them.
- **Gap in the middle** (some rows below the current max are missing, but newer rows
  already synced fine): lowering the cursor to before the gap means **everything**
  above the gap gets re-pulled too, not just the missing rows — confirm the target
  table won't end up with duplicates for rows that already made it through (no unique
  constraint ties a row back to its source `RowID` today, on most target tables)
  before doing this.
- Either way, if CCRISB2B's own eligibility window has already moved past those rows
  (e.g. a "yesterday only" filter), adjusting `SyncRunHistory` alone won't bring them
  back — that part has to be fixed on the **CCRISB2B side**, coordinating with
  whoever owns `usp_GetBCBNewData`. CBMSB2BLink has no way to force the source proc
  to return a specific `RowID` range on its own.
- Deleting/editing rows in the actual target table (e.g. removing genuinely bad rows)
  has **no effect** on the resume cursor either way now — `SyncRunHistory` is the only
  thing that matters for "where does the next run start."

## Interpreting a failure email

Subject: `[CBMSB2BLink] Sync FAILED for BCB_NEW on <host>`. Body has the run's
started/completed time, records read/inserted so far, and the full exception. Common
causes:

- **Cannot connect to CCRISB2B** — network/firewall issue, or the source server is down.
  Nothing was read, so nothing to lose; safe to wait and let the next scheduled run
  retry. If this persists, switch to the HTTP fallback bridge — see "Enabling the HTTP
  fallback bridge" below.
- **Cannot connect to CBMS / transaction failure** — check CBMS availability and that
  `dbo.SyncRunHistory` and `dbo.BcbRecordTableType` exist (see
  `sql/01_CreateSyncRunHistory_CBMS.sql`). If the source proc marks rows as sent the
  moment they're read (see ARCHITECTURE.md, "Failure & recovery scenarios"), this
  failure mode may mean those rows are now gone from CCRISB2B's "unsent" queue without
  ever landing in CBMS — check with whoever owns `usp_GetBCBNewData` whether that's
  possible with the current implementation, and whether those specific rows need to be
  manually re-flagged as unsent.
- **Lock held / "Skipped: another run is already in progress"** — a previous run is
  still executing (or crashed without releasing the lock file, unlikely since the OS
  releases the file handle on process exit). Check for a stuck process; delete
  `%ProgramData%\CBMSB2BLink\run.lock` only if you've confirmed no CBMSB2BLink process
  is actually running.
- **Config validation error at startup** — a required setting is missing/invalid; the
  log states which one. No connection is attempted in this case.

## Enabling the HTTP fallback bridge

Use this when CCRISB2B is confirmed unreachable via direct SQL and it's not clearing up
quickly (network maintenance, firewall change, extended outage).

1. Confirm `CBMSB2BLink.FallbackBridge.Api` is running near CCRISB2B and reachable:
   `curl http://<bridge-host>:<port>/healthz` should return `{"status":"ok"}`.
2. In `CBMSB2BLink.Console`'s `appsettings.json` (or an environment override), set:
   ```json
   "Sync": { "SourceMode": "Http" },
   "FallbackBridge": { "BaseUrl": "http://<bridge-host>:<port>/", "ApiKey": "..." }
   ```
   `FallbackBridge:ApiKey` must match the bridge's own `Bridge:ApiKey`.
3. Run (or wait for the next scheduled run). Behavior is otherwise identical — same
   `SyncRunHistory` rows, same transactional guarantees; only *how new rows are read*
   changes.
4. Once direct SQL to CCRISB2B is confirmed working again, set `Sync:SourceMode` back
   to `Sql` (or remove it — that's the default). This is a manual switch, not automatic.

## Checking the monitoring dashboard

Open `http://<monitoring-host>:<port>/` in a browser — shows the last synced
`ROWID`/`CMS_NO`, health status, and recent run history for each configured
`SyncKey`, all derived from `SyncRunHistory`. Same data as the "Checking status" SQL
queries above, refreshed automatically every 30s. `GET /api/status` and `GET
/api/runs` are also usable directly (e.g. from another monitoring tool) if a raw JSON
feed is more useful than the page.

## Troubleshooting checklist

1. Is the scheduled task actually running? (Task Scheduler → History)
2. What's the latest `SyncRunHistory` row say? (`Status`, `ErrorMessage`)
3. Is CCRISB2B reachable from the task's host? (`sqlcmd`/`Test-NetConnection`)
4. Is CBMS reachable, and do `SyncRunHistory`/`BcbRecordTableType` exist?
5. Are the connection strings still valid (password rotation)?
6. Check `C:\ProgramData\CBMSB2BLink\logs\` for the full stack trace.
