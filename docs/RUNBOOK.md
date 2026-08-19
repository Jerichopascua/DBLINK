# CBMSB2BLink — Runbook

## Manual run

```powershell
cd C:\Apps\CBMSB2BLink
.\CBMSB2BLink.exe
echo $LASTEXITCODE   # 0 = success or no-new-data, 1 = failure
```

Or from source: `dotnet run --project src/CBMSB2BLink.Console`.

## Checking status

```sql
-- Current watermark
SELECT * FROM dbo.SyncControl WHERE SyncKey = 'BCB_NEW';

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

## Resetting / adjusting the watermark

Only do this deliberately — it changes what gets re-pulled from CCRISB2B.

```sql
-- Re-process everything from a specific ROWID forward (e.g. after confirming a gap):
UPDATE dbo.SyncControl SET LastRowId = 12345 WHERE SyncKey = 'BCB_NEW';
```

Because the destination insert and watermark update commit together in one transaction
(see ARCHITECTURE.md), you should never need to do this after an ordinary failure — a
failed run leaves the watermark untouched and the next scheduled run simply retries the
same range. Only adjust it manually if you've independently confirmed CBMS is missing
rows that the watermark thinks are already synced (e.g. after a CBMS restore).

## Interpreting a failure email

Subject: `[CBMSB2BLink] Sync FAILED for BCB_NEW on <host>`. Body has the run's
started/completed time, records read/inserted so far, and the full exception. Common
causes:

- **Cannot connect to CCRISB2B** — network/firewall issue, or the source server is down.
  Watermark is untouched; safe to wait and let the next scheduled run retry. If this
  persists, this is the scenario the HTTP fallback bridge (see ARCHITECTURE.md, Phase 2)
  is meant to cover — not yet built.
- **Cannot connect to CBMS / transaction failure** — check CBMS availability and that
  `dbo.SyncControl`, `dbo.SyncRunHistory`, and `dbo.BcbRecordTableType` exist (see
  `sql/01_CreateSyncControlAndRunHistory_CBMS.sql`).
- **Lock held / "Skipped: another run is already in progress"** — a previous run is
  still executing (or crashed without releasing the lock file, unlikely since the OS
  releases the file handle on process exit). Check for a stuck process; delete
  `%ProgramData%\CBMSB2BLink\run.lock` only if you've confirmed no CBMSB2BLink process
  is actually running.
- **Config validation error at startup** — a required setting is missing/invalid; the
  log states which one. No connection is attempted in this case.

## Troubleshooting checklist

1. Is the scheduled task actually running? (Task Scheduler → History)
2. What's the latest `SyncRunHistory` row say? (`Status`, `ErrorMessage`)
3. Is CCRISB2B reachable from the task's host? (`sqlcmd`/`Test-NetConnection`)
4. Is CBMS reachable, and do `SyncControl`/`SyncRunHistory`/`BcbRecordTableType` exist?
5. Are the connection strings still valid (password rotation, DPAPI blob generated on
   the wrong machine)?
6. Check `C:\ProgramData\CBMSB2BLink\logs\` for the full stack trace.
