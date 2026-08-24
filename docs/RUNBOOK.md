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

There's no watermark to reset here — CBMSB2BLink always asks the source proc for
"what's new" starting from `@LastRowId = 0` and trusts whatever it gets back (see
ARCHITECTURE.md, "No CBMS-side watermark"). If CBMS is confirmed missing rows that
CCRISB2B has, the fix has to happen on the **CCRISB2B side**: whatever mechanism
`usp_GetBCBNewData` uses to track "already sent" (a status flag, a sent-timestamp,
etc.) needs those specific rows marked as un-sent again, so the next run's query
picks them up. Coordinate with whoever owns that proc — CBMSB2BLink has no way to
force a re-pull of a specific `ROWID` range on its own.

Also worth checking before assuming rows were lost: `BCB_NEW` has no unique
constraint tying a row back to its source `ROWID` today, so if a re-send does happen,
confirm it won't create duplicates for rows that already made it through.

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
