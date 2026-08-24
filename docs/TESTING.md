# CBMSB2BLink — Local End-to-End Testing

This is the procedure for testing the real sync flow against two scratch databases on
your own machine — no access to real CCRISB2B/CBMS required. It's what was used to
validate the app during development; the "worked example" section at the bottom shows
actual output from that run.

> CBMSB2BLink keeps no CBMS-side watermark (see `ARCHITECTURE.md`, "No CBMS-side
> watermark") — resume/dedup tracking lives entirely in the CCRISB2B-side stored
> procedure. The scratch `usp_GetBCBNewData` created by
> `sql/dev-seed_CCRISB2B_LocalTesting.sql` demonstrates one pattern for that (a `Sent`
> flag, flipped as rows are read) purely so these scenarios remain meaningful locally —
> a real production proc may track it differently.

## 1. Unit tests (no database needed)

```powershell
cd D:\git\scb\DBLink
dotnet test CBMSB2BLink.slnx
```

Covers `SyncEngine` orchestration against mocked repositories — happy path, no-new-data,
source unreachable, destination-insert rollback, multi-page batching.

## 2. Prerequisites for the end-to-end test

- A reachable SQL Server instance (local SQL Express/LocalDB/Docker `mssql` all work).
  If it's a local `SQLEXPRESS` service, make sure it's actually **running**:
  `Get-Service 'MSSQL$SQLEXPRESS'` — start it (as admin) with
  `Start-Service 'MSSQL$SQLEXPRESS'` if it's stopped.
- `sqlcmd` available on PATH (ships with SQL Server tools / ODBC driver installs).

## 3. Create two scratch databases

```powershell
sqlcmd -S "YOUR_SERVER\SQLEXPRESS" -U sa -P "YOUR_PASSWORD" -C -Q "IF DB_ID('CCRISB2B') IS NULL CREATE DATABASE CCRISB2B; IF DB_ID('CBMS') IS NULL CREATE DATABASE CBMS;"
```

## 4. Apply schema + dummy data

```powershell
# CCRISB2B side: tblRPT, usp_GetBCBNewData, 25 seeded rows
sqlcmd -S "YOUR_SERVER\SQLEXPRESS" -U sa -P "YOUR_PASSWORD" -C -i "sql\dev-seed_CCRISB2B_LocalTesting.sql"

# CBMS side: SyncRunHistory, BcbRecordTableType
sqlcmd -S "YOUR_SERVER\SQLEXPRESS" -U sa -P "YOUR_PASSWORD" -C -d CBMS -i "sql\01_CreateSyncRunHistory_CBMS.sql"

# CBMS side: BCB_NEW (only needed for a scratch DB — real CBMS already has it)
sqlcmd -S "YOUR_SERVER\SQLEXPRESS" -U sa -P "YOUR_PASSWORD" -C -i "sql\dev-seed_CBMS_LocalTesting.sql"
```

## 5. Point the app at the scratch databases

Copy `src/CBMSB2BLink.Console/appsettings.Development.json.example` to
`appsettings.Development.json` (same folder — it's gitignored, so your real password
never gets committed) and fill in your server/credentials. `Email:EnableOnFailure: false`
keeps the failure-test step below from trying to actually send mail.

Build once so the file gets copied to the output folder (the `.csproj` copies any
`appsettings.*.json` automatically):

```powershell
dotnet build src/CBMSB2BLink.Console
```

## 6. Run the scenarios

All runs: `cd` into the build output folder and set `DOTNET_ENVIRONMENT=Development`
first so the app picks up your scratch connection strings.

```powershell
cd src\CBMSB2BLink.Console\bin\Debug\net6.0
$env:DOTNET_ENVIRONMENT = "Development"
.\CBMSB2BLink.exe
echo $LASTEXITCODE
```

**a) Happy path (first run)** — pulls all 25 seeded rows, exit code 0.

**b) No-new-data (rerun immediately)** — same command again; should log
`No new records for BCB_NEW.` and touch nothing.

**c) Incremental sync** — insert a couple more rows into `CCRISB2B.dbo.tblRPT`, then run
again; only the new rows should sync.

```sql
INSERT INTO CCRISB2B.dbo.tblRPT (IDNO, CREATEDATE, AMOUNT)
VALUES ('9800000099', SYSDATETIME(), 500.00);
```

**d) Failure path** — temporarily point `ConnectionStrings:CcrisB2B` at something
unreachable (wrong port is an easy way: append `,1;` after the server name, or add
`Connect Timeout=3;` to fail fast) and rerun. Confirm: exit code 1, a `Failed` row lands
in `SyncRunHistory`. Since the connection never opens, `usp_GetBCBNewData` never
executes, so no rows in `tblRPT` get marked `Sent = 1` either — confirm that too
(`SELECT COUNT(*) FROM tblRPT WHERE Sent = 0` should be unchanged from before the
failed run). Revert the connection string afterwards.

**e) CBMS-side failure (the risky one)** — temporarily point `ConnectionStrings:Cbms`
at something unreachable instead, with new rows waiting, and rerun. Confirm: exit code
1, a `Failed` row lands in `SyncRunHistory`, but — unlike the source-side failure above
— check `tblRPT`: because this scratch proc marks `Sent = 1` the moment rows are read
(before CBMSB2BLink even attempts the CBMS insert), those rows are now marked sent in
CCRISB2B despite never having reached `BCB_NEW`. This is the exact trade-off documented
in `ARCHITECTURE.md` under "Failure & recovery scenarios" — worth reproducing once so
it's not a surprise in production. Revert the connection string afterwards; the
already-marked rows will need to be manually reset (`UPDATE tblRPT SET Sent = 0 WHERE
ROWID IN (...)`) to be re-synced.

## 7. Verify against the database directly

```sql
-- Most recent run per key (its SourceRowIdTo/CmsNoTo is "how far did this run get")
SELECT TOP 1 * FROM CBMS.dbo.SyncRunHistory WHERE SyncKey = 'BCB_NEW' ORDER BY RunId DESC;

-- Every run so far, in order
SELECT RunId, Status, SourceRowIdFrom, SourceRowIdTo, CmsNoFrom, CmsNoTo,
       RecordsInserted, DurationSeconds
FROM CBMS.dbo.SyncRunHistory ORDER BY RunId;

-- The actual synced data
SELECT TOP 5 CMS_NO, IDNO, CREATEDATE, AMOUNT
FROM CBMS.dbo.BCB_NEW ORDER BY CMS_NO DESC;
```

## 8. Testing the HTTP fallback bridge

Reuses the same scratch `CCRISB2B` database from steps 3–4 — no extra schema needed.

1. Copy `src/CBMSB2BLink.FallbackBridge.Api/appsettings.Development.json.example` to
   `appsettings.Development.json` in that same folder, fill in your `CcrisB2B`
   connection string, and pick an `ApiKey` (any string — it's a shared secret, not a
   real credential format).
2. Build and run it:
   ```powershell
   dotnet build src/CBMSB2BLink.FallbackBridge.Api
   cd src\CBMSB2BLink.FallbackBridge.Api\bin\Debug\net6.0
   $env:ASPNETCORE_ENVIRONMENT = "Development"
   $env:ASPNETCORE_URLS = "http://localhost:5145"
   dotnet CBMSB2BLink.FallbackBridge.Api.dll
   ```
3. In another shell, confirm auth and data:
   ```powershell
   curl http://localhost:5145/healthz                                                    # {"status":"ok"}
   curl -H "X-Api-Key: wrong" "http://localhost:5145/api/bcb-new?lastRowId=0&batchSize=5" # 401
   curl -H "X-Api-Key: <your key>" "http://localhost:5145/api/bcb-new?lastRowId=0&batchSize=5"
   ```
4. Point `CBMSB2BLink.Console`'s `appsettings.Development.json` at the bridge (see
   RUNBOOK.md, "Enabling the HTTP fallback bridge") and rerun `CBMSB2BLink.exe`. It
   should sync identically to the direct-SQL path — same log line shape, same
   `SyncRunHistory` behavior. Repeat the no-new-data and incremental scenarios from
   step 6 over HTTP to confirm parity.
5. Set `Sync:SourceMode` back to `Sql` (or delete the key) when done.

## 9. Testing the monitoring dashboard

Reuses the same scratch `CBMS` database — no extra schema needed, since it reads the
tables already populated by the steps above.

1. Copy `src/CBMSB2BLink.Monitoring.Api/appsettings.Development.json.example` to
   `appsettings.Development.json`, fill in your `Cbms` connection string.
2. Build and run it:
   ```powershell
   dotnet build src/CBMSB2BLink.Monitoring.Api
   cd src\CBMSB2BLink.Monitoring.Api\bin\Debug\net6.0
   $env:ASPNETCORE_ENVIRONMENT = "Development"
   $env:ASPNETCORE_URLS = "http://localhost:5155"
   dotnet CBMSB2BLink.Monitoring.Api.dll
   ```
3. Open `http://localhost:5155/` in a browser — should show the health badge, the
   most recent run's `LastRowId`/`LastCmsNo`, and the full run history, matching the
   SQL queries in step 7 exactly. Also check the raw endpoints:
   ```powershell
   curl http://localhost:5155/healthz
   curl http://localhost:5155/api/sync-keys
   curl "http://localhost:5155/api/status?syncKey=BCB_NEW"
   curl "http://localhost:5155/api/runs?syncKey=BCB_NEW&take=10"
   ```

## 10. Simulating a large backlog (capacity testing)

Reuses the same scratch `CCRISB2B` database — no new schema needed.

```powershell
sqlcmd -S "YOUR_SERVER\SQLEXPRESS" -U sa -P "YOUR_PASSWORD" -C -i "sql\dev-seed-bigdata_CCRISB2B_LocalTesting.sql"
```

Inserts 500,000 unsent rows (edit `@RowCount` in the script for a different volume) in
a couple of seconds using a set-based generator — **do not** try to scale up the small
seed script's row-by-row `WHILE` loop for this; it's fine for 25/5,000 rows but far too
slow at six figures. Then run `CBMSB2BLink.exe` as usual and time it — see
`ARCHITECTURE.md`, "Capacity & limits" for a real measured result (500K rows, ~17.5s
locally) and how to reason about `BatchSize`/`MaxRunDurationSeconds` headroom for your
actual expected backlog and network conditions.

---

## Worked example (actual results from a real run)

> **Historical** — these runs predate the removal of the `SyncControl` watermark table
> (see `ARCHITECTURE.md`, "No CBMS-side watermark"); the app at the time read/advanced
> a `LastRowId` in CBMS the way this section describes. Kept for reference on the
> shape of a real run (timings, log lines, the transactional-failure proof), but the
> watermark-specific claims below (`SyncControl` advancing/not advancing) no longer
> apply to the current design — CBMSB2BLink now has no watermark to advance, and the
> "what gets re-pulled after a failure" behavior instead depends on the CCRISB2B-side
> proc, per scenarios (d)/(e) above.

| Run | Scenario | Result |
|---|---|---|
| 1 | Initial load, 25 seeded rows (watermark starts at 0) | `Success`, 24 rows (RowId 1–24), 2072ms |
| 2 | Immediate rerun | `NoNewData`, 0 rows, 997ms |
| 3 | 3 new rows inserted, rerun | `Success`, 3 rows (RowId 25–27), 654ms |
| 4 | Source connection pointed at bad port | `Failed`, watermark stayed at 27, 3597ms |
| 5 | 2 new rows inserted, rerun | `Success`, 2 rows (RowId 28–29), 591ms |

Console output for run 5:
```
[17:39:21 INF] Starting sync for BCB_NEW from LastRowId=27
[17:39:21 INF] Sync succeeded for BCB_NEW: 2 records, RowId 28-29, CmsNo 28-29, 591ms
```

Note on run 1: the seeded table's `IDENTITY` started at `ROWID=0` (a `DBCC CHECKIDENT
RESEED` artifact from an earlier draft of the seed script — since fixed in
`dev-seed_CCRISB2B_LocalTesting.sql`, which now relies on `TRUNCATE TABLE`'s automatic
identity reset instead). Because the watermark defaults to `0` and the query is
`ROWID > @LastRowId`, `ROWID=0` was correctly treated as already-synced and excluded —
25 seeded rows, 24 synced. This is expected, exclusive-boundary behavior, not a bug; it's
called out here so it doesn't look like an off-by-one error in the app when reproducing
this test.

Run 4 (`Failed`) is the important one to reproduce: it proves the transactional design
in `SyncEngine` — insert + watermark update + history all commit together — so a failed
run never advances or corrupts `SyncControl`, and the next run safely retries the same
range.

### Phase 2 worked example (fallback bridge + dashboard)

Databases reset to a clean baseline (25 seeded rows, `ROWID 1–25`, watermark zeroed)
before this round, using the fixed seed script.

| Run | Scenario | Result |
|---|---|---|
| 1 | Full load via `Sync:SourceMode=Http` through the bridge | `Success`, 25 rows (RowId 1–25), 969ms |
| 2 | Immediate rerun via HTTP | `NoNewData`, 0 rows, 523ms |
| 3 | 1 new row inserted, rerun via HTTP | `Success`, 1 row (RowId 26), 596ms |

Confirms the HTTP path produces byte-for-byte the same outcome as the direct-SQL path
tested above — same log line shape, same `SyncRunHistory` rows, same watermark
semantics. `curl` against the bridge directly (before running the console app) also
confirmed: missing/wrong `X-Api-Key` → `401`, correct key → `200` with the expected
CCRISB2B rows as camelCase JSON.

The dashboard, pointed at the same CBMS database after run 3 above, showed: `Healthy`
badge, `LAST ROWID 26`, `LAST CMS_NO 26`, `LAST RUN STATUS Success`, and all 3 runs in
the history table — matching the direct SQL query exactly (screenshot-verified in a
real browser, not just curl).

**Bug found and fixed during this pass:** `DashboardOptions.SyncKeys` originally
defaulted to `["BCB_NEW"]` in the C# property initializer. `Microsoft.Extensions.Configuration`
*appends* config-supplied array values onto a pre-existing non-empty default array
rather than replacing it — so with both the default and `appsettings.json`'s
`Dashboard:SyncKeys: ["BCB_NEW"]` in play, `/api/sync-keys` returned `["BCB_NEW","BCB_NEW"]`.
Fixed by defaulting to an empty array and applying the `"BCB_NEW"` fallback via
`PostConfigure` after binding instead (see `DashboardOptions.cs`). Worth remembering for
any other array-typed option added later in this codebase.
