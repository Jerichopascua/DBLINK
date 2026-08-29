using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CBMSB2BLink.Core.Abstractions;
using CBMSB2BLink.Core.Models;
using CBMSB2BLink.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CBMSB2BLink.Core;

/// <summary>
/// Orchestrates one CBMSB2BLink run: acquire one process-level lock, then run every
/// configured job in Sync:Jobs, in order, isolated (one job failing does not stop the
/// others). Each job is paged and committed **one page at a time**: before every
/// single source call — not just once per run — SyncEngine re-reads
/// IResumeCursorRepository.GetLastRowIdAsync (MAX(SourceRowIdTo) FROM
/// dbo.SyncRunHistory, an app-owned table — deliberately NOT read from the target
/// table's own data, since a BAU target's "key" column can be a server-generated
/// IDENTITY unrelated to the source RowID) to seed that page's @LastRowId, pulls one
/// page, inserts it, and commits it (insert + its own SyncRunHistory row) in one
/// transaction before moving on to the next page. Because the SyncRunHistory row is
/// written in the exact same transaction as the page's insert, the cursor can never
/// drift from what was actually committed — this makes it self-healing at page
/// granularity, not just run granularity: a crash after page 2 of 5 leaves pages 1-2
/// durably committed, and the very next call (whether later in the same run or a
/// fresh run) re-derives its starting point from SyncRunHistory. @LastRowId is the
/// only dedup mechanism in the pipeline (the source query has no sent-log of its
/// own) — the source query's own filtering is expected to be strictly increasing
/// with RowID.
/// </summary>
public sealed class SyncEngine
{
    private readonly ISourceRepository _sourceRepository;
    private readonly IDestinationRepository _destinationRepository;
    private readonly ISyncRunHistoryRepository _syncRunHistoryRepository;
    private readonly IResumeCursorRepository _resumeCursorRepository;
    private readonly ITargetUnitOfWorkFactory _unitOfWorkFactory;
    private readonly INotificationService _notificationService;
    private readonly IRunLock _runLock;
    private readonly SyncOptions _options;
    private readonly ILogger<SyncEngine> _logger;

    public SyncEngine(
        ISourceRepository sourceRepository,
        IDestinationRepository destinationRepository,
        ISyncRunHistoryRepository syncRunHistoryRepository,
        IResumeCursorRepository resumeCursorRepository,
        ITargetUnitOfWorkFactory unitOfWorkFactory,
        INotificationService notificationService,
        IRunLock runLock,
        IOptions<SyncOptions> options,
        ILogger<SyncEngine> logger)
    {
        _sourceRepository = sourceRepository;
        _destinationRepository = destinationRepository;
        _syncRunHistoryRepository = syncRunHistoryRepository;
        _resumeCursorRepository = resumeCursorRepository;
        _unitOfWorkFactory = unitOfWorkFactory;
        _notificationService = notificationService;
        _runLock = runLock;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<List<SyncRunResult>> RunAsync(CancellationToken cancellationToken)
    {
        var results = new List<SyncRunResult>();

        using var heldLock = await _runLock.TryAcquireAsync(cancellationToken);
        if (heldLock is null)
        {
            _logger.LogWarning("Another CBMSB2BLink run already holds the lock. Exiting without action.");
            var lockResult = new SyncRunResult
            {
                SyncKey = "(lock)",
                StartedUtc = DateTime.UtcNow,
                CompletedUtc = DateTime.UtcNow,
                Status = SyncRunStatus.Failed,
                ErrorMessage = "Skipped: another run is already in progress (lock held)."
            };
            results.Add(lockResult);
            await TryNotifyFailureAsync(results, cancellationToken);
            return results;
        }

        foreach (var job in _options.Jobs)
        {
            var result = await RunJobAsync(job, cancellationToken);
            results.Add(result);
        }

        var failed = results.Where(r => r.Status == SyncRunStatus.Failed).ToList();
        if (failed.Count > 0)
        {
            await TryNotifyFailureAsync(failed, cancellationToken);
        }

        return results;
    }

    /// <summary>
    /// Runs one job to completion: pages through its source, committing each page to
    /// the target as soon as it's pulled (see class summary), until a short/empty page
    /// comes back or job.BatchAllowedMaxRecord is reached. Returns one aggregate
    /// SyncRunResult summarizing every page committed during this call — the
    /// per-page detail lives in SyncRunHistory (one row per committed page), not in
    /// this return value.
    /// </summary>
    private async Task<SyncRunResult> RunJobAsync(SyncJobOptions job, CancellationToken cancellationToken)
    {
        var runStopwatch = Stopwatch.StartNew();
        var aggregate = new SyncRunResult
        {
            SyncKey = job.JobKey,
            StartedUtc = DateTime.UtcNow
        };

        try
        {
            _logger.LogInformation("Starting sync for {JobKey}", job.JobKey);

            await _syncRunHistoryRepository.EnsureSchemaAsync(job.Target.ConnectionString, cancellationToken);

            var pagesCommitted = 0;
            var totalRead = 0;
            var totalInserted = 0;
            long? overallFrom = null;
            long? overallTo = null;

            while (true)
            {
                var cursor = await _resumeCursorRepository.GetLastRowIdAsync(job.Target.ConnectionString, job.JobKey, cancellationToken);

                var pageStopwatch = Stopwatch.StartNew();
                var page = await _sourceRepository.GetNewRecordsAsync(job.Source, cursor, job.BatchSize, job.CommandTimeoutSeconds, cancellationToken);

                if (page.Columns.Count != job.Target.Columns.Count)
                {
                    throw new InvalidOperationException(
                        $"Job {job.JobKey}: source query returned {page.Columns.Count} column(s) but Target.Columns configures {job.Target.Columns.Count}. Fix the job's Target.Columns list or the source query.");
                }

                if (page.Rows.Count == 0)
                {
                    if (pagesCommitted == 0)
                    {
                        _logger.LogInformation("No new records for {JobKey}.", job.JobKey);
                        var noData = new SyncRunResult
                        {
                            SyncKey = job.JobKey,
                            StartedUtc = aggregate.StartedUtc,
                            Status = SyncRunStatus.NoNewData,
                            CompletedUtc = DateTime.UtcNow,
                            DurationSeconds = runStopwatch.Elapsed.TotalSeconds
                        };

                        await using var noopUow = _unitOfWorkFactory.Create(job.Target.ConnectionString);
                        await noopUow.InitializeAsync(cancellationToken);
                        await _syncRunHistoryRepository.RecordRunAsync(noopUow, noData, cancellationToken);
                        await noopUow.CommitAsync(cancellationToken);

                        return noData;
                    }

                    break;
                }

                var pageFrom = Convert.ToInt64(page.Rows[0][0]);
                var pageTo = Convert.ToInt64(page.Rows[^1][0]);

                var pageResult = new SyncRunResult
                {
                    SyncKey = job.JobKey,
                    StartedUtc = DateTime.UtcNow,
                    RecordsRead = page.Rows.Count,
                    SourceRowIdFrom = pageFrom,
                    SourceRowIdTo = pageTo
                };

                await using var uow = _unitOfWorkFactory.Create(job.Target.ConnectionString);
                await uow.InitializeAsync(cancellationToken);
                try
                {
                    var insertResult = await _destinationRepository.InsertBatchAsync(uow, job.Target.Table, job.Target.Columns, page, job.CommandTimeoutSeconds, cancellationToken);
                    pageResult.RecordsInserted = insertResult.RecordsInserted;
                    // The key is copied straight through from source column 0 to
                    // Target.Columns[0], never target-generated — so the inserted range
                    // is always identical to SourceRowIdFrom/To, already computed above.
                    pageResult.CmsNoFrom = pageFrom;
                    pageResult.CmsNoTo = pageTo;
                    pageResult.Status = SyncRunStatus.Success;
                    pageResult.CompletedUtc = DateTime.UtcNow;
                    pageResult.DurationSeconds = pageStopwatch.Elapsed.TotalSeconds;

                    await _syncRunHistoryRepository.RecordRunAsync(uow, pageResult, cancellationToken);
                    await uow.CommitAsync(cancellationToken);

                    _logger.LogInformation(
                        "Page committed for {JobKey}: {RecordsInserted} records, RowId {From}-{To}, {DurationSeconds}s",
                        job.JobKey, pageResult.RecordsInserted, pageFrom, pageTo, pageResult.DurationSeconds);
                }
                catch
                {
                    await uow.RollbackAsync(cancellationToken);
                    throw;
                }

                pagesCommitted++;
                totalRead += page.Rows.Count;
                totalInserted += pageResult.RecordsInserted;
                overallFrom ??= pageFrom;
                overallTo = pageTo;

                if (page.Rows.Count < job.BatchSize)
                {
                    break;
                }

                if (totalRead >= job.BatchAllowedMaxRecord)
                {
                    _logger.LogInformation(
                        "Job {JobKey}: reached BatchAllowedMaxRecord ({Max}) after {TotalRows} rows — stopping for this run; the rest resumes on the next call.",
                        job.JobKey, job.BatchAllowedMaxRecord, totalRead);
                    break;
                }
            }

            aggregate.RecordsRead = totalRead;
            aggregate.RecordsInserted = totalInserted;
            aggregate.SourceRowIdFrom = overallFrom;
            aggregate.SourceRowIdTo = overallTo;
            aggregate.CmsNoFrom = overallFrom;
            aggregate.CmsNoTo = overallTo;
            aggregate.Status = SyncRunStatus.Success;
            aggregate.CompletedUtc = DateTime.UtcNow;
            aggregate.DurationSeconds = runStopwatch.Elapsed.TotalSeconds;

            _logger.LogInformation(
                "Sync succeeded for {JobKey}: {RecordsInserted} records across {PageCount} page(s), RowId {From}-{To}, {DurationSeconds}s",
                job.JobKey, totalInserted, pagesCommitted, overallFrom, overallTo, aggregate.DurationSeconds);

            return aggregate;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync run failed for {JobKey}", job.JobKey);
            aggregate.Status = SyncRunStatus.Failed;
            aggregate.ErrorMessage = ex.ToString();
            aggregate.CompletedUtc = DateTime.UtcNow;
            aggregate.DurationSeconds = runStopwatch.Elapsed.TotalSeconds;

            await TryRecordFailedRunAsync(job, aggregate, cancellationToken);
            return aggregate;
        }
    }

    private async Task TryRecordFailedRunAsync(SyncJobOptions job, SyncRunResult result, CancellationToken cancellationToken)
    {
        try
        {
            await _syncRunHistoryRepository.RecordFailedRunAsync(job.Target.ConnectionString, result, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record failed-run history for {JobKey}", result.SyncKey);
        }
    }

    private async Task TryNotifyFailureAsync(IReadOnlyList<SyncRunResult> failedResults, CancellationToken cancellationToken)
    {
        try
        {
            await _notificationService.SendFailureAsync(failedResults, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send failure notification for {Count} failed job(s)", failedResults.Count);
        }
    }
}
