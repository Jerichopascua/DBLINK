USE CBMS

INSERT INTO SyncRunHistory (
    SyncKey,
    StartedUtc,
    CompletedUtc,
    Status,
    SourceRowIdFrom,
    SourceRowIdTo,
    CmsNoFrom,
    CmsNoTo,
    RecordsRead,
    RecordsInserted,
    ErrorMessage,
    HostMachine,
    DurationSeconds
)
SELECT TOP (200) 
    SyncKey,
    StartedUtc,
    CompletedUtc,
    Status,
    SourceRowIdFrom,
    SourceRowIdTo,
    CmsNoFrom,
    CmsNoTo,
    RecordsRead,
    RecordsInserted,
    ErrorMessage,
    HostMachine,
    DurationSeconds
FROM SyncRunHistory;

--SELECT * FROM SyncRunHistory ORDER BY 1 DESC