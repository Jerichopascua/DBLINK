# CBMSB2BLink — Production Setup & Critical Ownership

This is the entry point for taking CBMSB2BLink from "builds and passes tests" to
"running against real CCRISB2B/CBMS." It's split into two parts: code **you** need to
write that isn't in this repo, and deployment/config steps for what **is** in this
repo. Read "Part A" first — it's the part most likely to cause a real problem if
skipped, not just a config error.

## Part A — Critical code you own (not built by this repo)

### A.1 The real `usp_GetBCBNewData` on CCRISB2B — the most important piece

CBMSB2BLink keeps **no resume watermark**. Every run calls
`usp_GetBCBNewData @LastRowId = 0, @BatchSize = n` and trusts whatever comes back is
genuinely new (see `ARCHITECTURE.md`, "No CBMS-side watermark"). This means:

- **The proc must track "already sent" itself.** Nothing in CBMSB2BLink does this for
  you. If the real proc is deployed with the naive `WHERE ROWID > @LastRowId` shape
  from `sql/02_usp_GetBCBNewData_CCRISB2B.sql`, it will hand back the **same rows every
  run** and CBMSB2BLink will re-insert them into `BCB_NEW` (which has no dedup
  protection — see A.2). That template file is a contract/shape reference only, **not**
  a working implementation — its own header comment says so.
- **You have a design decision to make, deliberately**, not by default:
  | Pattern | How | Trade-off |
  |---|---|---|
  | Mark-on-read | Flag rows sent the moment the proc returns them (what the local test scaffolding in `sql/dev-seed_CCRISB2B_LocalTesting.sql` does) | Simple. But if CBMS is unreachable *after* the read and *before* the CBMS transaction commits, those rows are marked sent on the source side yet never landed in CBMS — **silently lost** unless someone manually un-marks them. Verified live: see `ARCHITECTURE.md`, "Failure & recovery scenarios." |
  | Ack-based | Only mark sent after some confirmation CBMSB2BLink actually finished processing that batch | Closes the gap above, but CBMSB2BLink's current interface (`ISourceRepository.GetNewRecordsAsync`) has no "confirm" call — this would need an interface change, not just a proc change. |

  Pick one on purpose. Mark-on-read is fine if occasional manual reconciliation after a
  CBMS outage is acceptable; if it isn't, the ack-based path needs to be scoped as
  actual CBMSB2BLink work, not just a CCRISB2B-side proc change.
- **Review column names/types against the real `tblRPT`** — the template assumes
  `ROWID BIGINT, IDNO VARCHAR(50), CREATEDATE DATETIME2, AMOUNT DECIMAL(18,2)`. Confirm
  this matches; `SqlSourceRepository`'s row mapping is hardcoded to these column names.

### A.2 Dedup safety net on `BCB_NEW` (strongly recommended)

`BCB_NEW` has no unique constraint tying a row back to its source `ROWID` — only
`CMS_NO IDENTITY PK`. If the chosen tracking pattern in A.1 can ever cause the same
source row to be sent twice (a manual re-flag after reconciling a mark-on-read gap, a
backfill, a bug), CBMSB2BLink will insert it twice. If this can't happen in your design
(and you're confident enough to bet on it), no action needed. Otherwise, add a
`SourceRowId BIGINT NOT NULL` column with a unique index to `BCB_NEW` and switch the
insert in `SqlDestinationRepository.InsertBatchAsync` to `INSERT ... WHERE NOT EXISTS`.
This is application code, not just SQL — flag it as a task if you want it, it isn't
built today.

### A.3 Confirm `BCB_NEW`'s real schema

The repo assumes `BCB_NEW (CMS_NO IDENTITY PK, IDNO VARCHAR(50), CREATEDATE DATETIME2,
AMOUNT DECIMAL(18,2))` per `StartPrompt.md`. `SqlDestinationRepository`'s `DataTable`/TVP
columns are hardcoded to this shape (`dbo.BcbRecordTableType` in
`sql/01_CreateSyncRunHistory_CBMS.sql`). If the real table's types differ (e.g. `IDNO`
is wider, `AMOUNT` has different precision), update the TVP definition and the insert
column list to match before going live.

## Part B — Setup / deployment checklist

### B.1 SQL objects

- [ ] Deploy your real `usp_GetBCBNewData` (Part A) to CCRISB2B.
- [ ] Run `sql/01_CreateSyncRunHistory_CBMS.sql` against real CBMS — creates
      `SyncRunHistory` (audit log) and `BcbRecordTableType` (insert plumbing). No
      watermark table to create; there isn't one anymore.
- [ ] Confirm `BCB_NEW` already exists with the expected shape (A.3) — this repo never
      creates it in production, only in the local scratch-DB test scripts.

### B.2 Configuration (`CBMSB2BLink.Console/appsettings.json`)

- [ ] `ConnectionStrings:CcrisB2B` / `:Cbms` — real server/database/credentials.
      Manage `appsettings.json` access/secrets separately (see `CONFIGURATION.md`).
- [ ] `Sync:BatchSize` / `Sync:MaxRunDurationSeconds` — defaults (5,000 / 1,800s) have
      comfortable headroom for six-figure backlogs on localhost (measured: 500K rows in
      17.5s — see `ARCHITECTURE.md`, "Capacity & limits"), but that number ignores real
      network latency between the app host and two separate SQL Server instances. Load
      test with `sql/dev-seed-bigdata_CCRISB2B_LocalTesting.sql` (`docs/TESTING.md` §10)
      against something closer to the real network topology if the expected first-sync
      backlog is large, and size these two settings from that, not from the localhost
      number.
- [ ] `Email` — real SMTP relay host/port, `EnableOnFailure: true`, real recipients.
      Confirm delivery actually works (see B.4) — a silently-broken failure path is
      worse than no failure path.
- [ ] `Serilog` — log path/retention defaults are usually fine; adjust
      `retainedFileCountLimit` if 30 days doesn't match your retention policy.

### B.3 Scheduling

- [ ] Publish: `dotnet publish src/CBMSB2BLink.Console -c Release -r win-x64
      --self-contained false -o <install path>`.
- [ ] Task Scheduler setup per `CONFIGURATION.md` — trigger interval, "run whether user
      is logged on or not," "do not start a new instance," and a "stop if runs longer
      than" safety net a bit above `MaxRunDurationSeconds`.

### B.4 Optional components — only deploy what you actually need

- [ ] `CBMSB2BLink.FallbackBridge.Api` — only if there's a real chance direct SQL to
      CCRISB2B becomes unreachable and a fallback path is wanted. Hosted **near
      CCRISB2B**, not near CBMS. Needs its own `Bridge:ApiKey` matching the console
      app's `FallbackBridge:ApiKey`.
- [ ] `CBMSB2BLink.Monitoring.Api` — read-only dashboard over `SyncRunHistory`. No
      built-in auth — put it behind a network ACL / IIS auth if it's reachable from
      anywhere beyond the internal network.

### B.5 Validate before pointing at real data

- [ ] Run the full local suite (`docs/TESTING.md`) against scratch databases first —
      happy path, no-new-data, incremental, both failure modes (source unreachable,
      CBMS unreachable), and the HTTP bridge / dashboard if deploying those.
- [ ] Reproduce the CBMS-unreachable scenario specifically (`docs/TESTING.md` §6e)
      against **your real proc's** tracking mechanism, not just the local test
      scaffolding's `Sent` flag — confirm you understand what state CCRISB2B and CBMS
      end up in in your actual implementation, and that it matches the choice made in
      A.1.
- [ ] `dotnet test CBMSB2BLink.slnx` — 20 unit tests, no database needed, quick sanity
      check after any local changes.

## Quick reference

| Question | See |
|---|---|
| How does a run actually work end to end? | `ARCHITECTURE.md` — "Run flow" |
| Why is there no watermark table? | `ARCHITECTURE.md` — "No CBMS-side watermark" |
| What happens on a crash / extended outage? | `ARCHITECTURE.md` — "Failure & recovery scenarios" |
| All config keys, hosting, Task Scheduler | `CONFIGURATION.md` |
| Day-2 operations, troubleshooting, log locations | `RUNBOOK.md` |
| Local scratch-DB testing procedure | `TESTING.md` |
