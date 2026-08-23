# BCB_NEW2 Pipeline Design

Status: approved by user (2026-08-23), ready for implementation planning.

## Summary

CBMSB2BLink's sync pipeline currently pulls rows from CCRISB2B's `dbo.tblRPT` via
`usp_GetBCBNewData` and inserts them into CBMS's `dbo.BCB_NEW` (`IDNO, CREATEDATE,
AMOUNT`). This is a **full cutover** to a new pipeline shape:

- Source: CCRISB2B `dbo.src_tblRetRpt` (+ `dbo.src_tblCRARawReport` for the
  FK-joined branch) — schema defined in `sql/source_CCRISB2B_01.sql`.
- Destination: CBMS `dbo.BCB_NEW2` — 12 `BCB_*` business columns, no PK/IDENTITY in
  the DDL as given.

The old `tblRPT` → `BCB_NEW` flow is retired: `BcbRecord`, the source proc contract,
and the destination insert all move to the new shape. `SyncEngine`'s orchestration
(lock → pull pages → insert batch → record history → notify on failure) is unchanged.

## Decisions made during brainstorming

1. **Full cutover**, not a switchable dual pipeline. Old dev-seed scripts and the old
   proc for `tblRPT`/`BCB_NEW` are deleted, not kept alongside the new ones.
2. **Dedup lives entirely in the CCRISB2B-side stored procedure**, not in the CBMS
   destination insert. This matches the project's existing documented architecture
   (`docs/ARCHITECTURE.md`, "No CBMS-side watermark") and rules out the
   `NOT IN (SELECT BCB_CMS_No FROM BCB_NEW2)`-in-the-INSERT idea raised earlier in
   conversation — `BCB_NEW2.BCB_CMS_No` and `@Records`' shape don't support that check
   safely, and it's redundant once the source proc guarantees no repeats.
3. **Sent-row tracking uses a separate CCRISB2B-side table**, `dbo.CbmsB2BLink_SentLog`,
   not a column added to `src_tblRetRpt`. `src_tblRetRpt`/`src_tblCRARawReport` are
   newly-created tables per the user but are treated as the shared business schema
   (real customer/report data with FKs), not a scratch table CBMSB2BLink owns — the
   tracking table keeps the sync concern out of it.

## CCRISB2B side

### `dbo.CbmsB2BLink_SentLog`

```sql
CREATE TABLE dbo.CbmsB2BLink_SentLog (
    RowID   INT NOT NULL PRIMARY KEY,   -- src_tblRetRpt.RowID already sent to CBMS
    SentUtc DATETIME2 NOT NULL
);
```

### `usp_GetBCBNewData` (replaces the `tblRPT`-based version)

Contract stays `@LastRowId BIGINT, @BatchSize INT` in; returns at most `@BatchSize`
rows ordered by `RowID` ascending. Preserves both branches from
`sql/source_CCRISB2B_01.sql` (direct `CCRIS_Status_Detailed`/`Date_Response_Detailed`
check on `src_tblRetRpt`; FK-joined check via `CRARawReportID` against
`src_tblCRARawReport.Status`/`DateResponse`), combined with:

- `WHERE RowID > @LastRowId AND RowID NOT IN (SELECT RowID FROM CbmsB2BLink_SentLog)`
- Mark-on-read: the same statement/transaction that selects the batch also inserts
  the returned `RowID`s into `CbmsB2BLink_SentLog`. This is the same tradeoff the
  existing test proc already documents (a CBMS-side failure between read and commit
  means that row is never retried) — accepted deliberately, consistent with current
  project precedent, not a new risk introduced by this change.

Output columns (aliased to match `BCB_NEW2`):

| Proc output | Source | BCB_NEW2 column |
|---|---|---|
| `RowID` | `src_tblRetRpt.RowID` | pagination key only, not inserted |
| `BCB_CMS_No` | `src_tblRetRpt.RowID` | `BCB_CMS_No` |
| `BCB_IdNo1` | `Cust_IDNo1` | `BCB_IdNo1` |
| `BCB_IdNo2` | `Cust_IDNo2` | `BCB_IdNo2` |
| `BCB_Name1` | `Cust_Name` | `BCB_Name1` |
| `BCB_DOB` | `CONVERT(VARCHAR(10), Cust_DateBR, 103)` | `BCB_DOB` |
| `BCB_Nationality` | `Cust_Nationality` | `BCB_Nationality` |
| `BCB_CreateDate` | `Date_Imported` | `BCB_CreateDate` |
| `BCB_LastUpdateBy` | `User_ID` | `BCB_LastUpdateBy` |
| `BCB_ENTKEY` | `Cust_Entity` | `BCB_ENTKEY` |
| `BCB_RefNo` | `RefNo` | `BCB_RefNo` |
| `BCB_SCR_Scored_TxnCode` | literal `'SCP'`/`'USC'` per branch | `BCB_SCR_Scored_TxnCode` |

`BCB_STATUS`/`BCB_CMS_Status` are not populated by the proc (no source mapping
identified) — left NULL on insert unless a real mapping surfaces during
implementation.

## CBMS side

### `dbo.BCB_NEW2`

Adds a surrogate PK for row integrity/indexing; `BCB_CMS_No` stays a plain
copied-over value (the source `RowID`), not a generated identity:

```sql
ALTER TABLE dbo.BCB_NEW2 ADD Id BIGINT IDENTITY(1,1) PRIMARY KEY;
```

### `dbo.BcbRecordTableType`

Replaces the 3-column TVP with the 11 `BCB_*` business columns (excluding
`BCB_STATUS`/`BCB_CMS_Status`, which the destination doesn't set on insert — leave
them out of the TVP too, or include as nullable if a future write path needs them;
default to excluding for now, matching "no source mapping identified" above).

### Destination insert (`SqlDestinationRepository`)

Plain `INSERT INTO dbo.BCB_NEW2 (...) SELECT ... FROM @Records` — no dedup filter,
per decision #2. `OUTPUT INSERTED.BCB_CMS_No` still used to report a range back
(chosen over `Id` because it's the value with cross-system meaning — see "Open
items"), but since `BCB_CMS_No` isn't server-generated,
`InsertBatchResult.CmsNoFrom/CmsNoTo` are populated from `MIN/MAX(BCB_CMS_No)` of the
inserted batch — which will equal `SourceRowIdFrom/To` for this pipeline. Kept as-is
(not removed from `SyncRunHistory`) to avoid an audit-table schema migration; the
duplication is accepted, not hidden.

## C# changes

- `CBMSB2BLink.Core.Models.BcbRecord`: replace `IdNo/CreateDate/Amount` with `RowId`
  (pagination key, = source `RowID`) + the 11 `BCB_*` fields (excluding
  `BCB_STATUS`/`BCB_CMS_Status`, per above).
- `SqlSourceRepository`: `BcbRecordRow` mapping updated to the new proc output
  columns; `StoredProcedureName` config key unchanged (`usp_GetBCBNewData`).
- `SqlDestinationRepository`: `DataTable` schema + INSERT statement updated to the
  new shape and table name (`BCB_NEW2`).
- `InsertBatchResult`: unchanged shape (`RecordsInserted`, `CmsNoFrom`, `CmsNoTo`),
  semantics of `CmsNoFrom/To` updated per above.
- Doc references (`docs/ARCHITECTURE.md`, `docs/CONFIGURATION.md`, `StartPrompt.md`)
  to `tblRPT`/`BCB_NEW`/the 3-column shape updated to match.

## Cleanup (delete, not deprecate-in-place)

- `sql/dev-seed_CCRISB2B_LocalTesting.sql` (tblRPT + old proc + 25 rows)
- `sql/dev-seed-bigdata_CCRISB2B_LocalTesting.sql` (tblRPT bulk seed)
- `sql/dev-seed_CBMS_LocalTesting.sql` (BCB_NEW creation)
- `sql/02_usp_GetBCBNewData_CCRISB2B.sql` (old proc template)

Git history preserves these; no need to keep stale copies that no longer match the
app once the cutover lands.

My existing new-pipeline seed scripts get adjusted to match the finalized schema:

- `sql/dev-seed-bigdata_CRARawReport_CCRISB2B_LocalTesting.sql` — add awareness of
  `CbmsB2BLink_SentLog` (leave it empty/unseeded so all rows look "unsent" for
  end-to-end testing, matching the fresh-table intent of the seed).
- `sql/dev-seed_BCB_NEW2_CBMS_LocalTesting.sql` — align columns with the finalized
  `BcbRecordTableType`/insert shape (surrogate PK, no `BCB_STATUS`/`BCB_CMS_Status`
  writes assumed by the app, though the seed script itself can still fill them with
  placeholder values since it simulates already-synced data, not app output).

## Tests

`SyncEngineTests`: mocks are shape-agnostic (`ISourceRepository`/`IDestinationRepository`
interfaces don't change), only the `Record(...)` test helper and field assertions need
updating to the new `BcbRecord` shape. No new test *behavior* required — the same
scenarios (happy path, no-new-data, source unreachable, destination rollback,
multi-page batching) still apply, just with new field names.

## Open items carried into implementation planning

- Exact final column list for `BcbRecordTableType` (whether to include
  `BCB_STATUS`/`BCB_CMS_Status` as nullable, unset columns, vs. omit entirely) —
  default to omit per this spec, revisit if implementation surfaces a need.
- Whether `OUTPUT INSERTED.BCB_CMS_No` or `OUTPUT INSERTED.Id` is more useful for the
  captured range in `SyncRunHistory` — default to `BCB_CMS_No` since it's the value
  with cross-system meaning (matches `SourceRowIdFrom/To`).
