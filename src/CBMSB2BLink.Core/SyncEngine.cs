using System;
using System.Collections.Generic;
using System.Data;
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
/// others). CBMSB2BLink does not track a resume watermark itself for any job — each
/// job's source query is responsible for knowing what's already been sent (see
/// ISourceRepository).
/// </summary>
public sealed class SyncEngine
{
    private readonly ISourceRepository _sourceRepository;
    private readonly IDestinationRepository _destinationRepository;
    private readonly ISyncRunHistoryRepository _syncRunHistoryRepository;
    private readonly ITargetUnitOfWorkFactory _unitOfWorkFactory;
    private readonly INotificationService _notificationService;
    private readonly IRunLock _runLock;
    private readonly SyncOptions _options;
    private readonly ILogger<SyncEngine> _logger;

    public SyncEngine(
        ISourceRepository sourceRepository,
        IDestinationRepository destinationRepository,
        ISyncRunHistoryRepository syncRunHistoryRepository,
        ITargetUnitOfWorkFactory unitOfWorkFactory,
        INotificationService notificationService,
        IRunLock runLock,
        IOptions<SyncOptions> options,
        ILogger<SyncEngine> logger)
    {
        _sourceRepository = sourceRepository;
        _destinationRepository = destinationRepository;
        _syncRunHistoryRepository = syncRunHistoryRepository;
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

    private async Task<SyncRunResult> RunJobAsync(SyncJobOptions job, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new SyncRunResult
        {
            SyncKey = job.JobKey,
            StartedUtc = DateTime.UtcNow
        };

        try
        {
            _logger.LogInformation("Starting sync for {JobKey}", job.JobKey);

            await _syncRunHistoryRepository.EnsureSchemaAsync(job.Target.ConnectionString, cancellationToken);

            var batch = await PullAllPagesAsync(job, cancellationToken);
            result.RecordsRead = batch.Rows.Count;

            if (batch.Rows.Count == 0)
            {
                _logger.LogInformation("No new records for {JobKey}.", job.JobKey);
                result.Status = SyncRunStatus.NoNewData;
                result.CompletedUtc = DateTime.UtcNow;
                result.DurationMs = (int)stopwatch.ElapsedMilliseconds;

                await using var noopUow = _unitOfWorkFactory.Create(job.Target.ConnectionString);
                await noopUow.InitializeAsync(cancellationToken);
                await _syncRunHistoryRepository.RecordRunAsync(noopUow, result, cancellationToken);
                await noopUow.CommitAsync(cancellationToken);

                return result;
            }

            result.SourceRowIdFrom = Convert.ToInt64(batch.Rows[0][0]);
            result.SourceRowIdTo = Convert.ToInt64(batch.Rows[batch.Rows.Count - 1][0]);

            await using var uow = _unitOfWorkFactory.Create(job.Target.ConnectionString);
            await uow.InitializeAsync(cancellationToken);
            try
            {
                var insertResult = await _destinationRepository.InsertBatchAsync(uow, job.Target.Table, job.Target.Columns, batch, job.CommandTimeoutSeconds, cancellationToken);
                result.RecordsInserted = insertResult.RecordsInserted;
                // The key is copied straight through from source column 0 to
                // Target.Columns[0], never target-generated — so the inserted range is
                // always identical to SourceRowIdFrom/To, already computed above.
                result.CmsNoFrom = result.SourceRowIdFrom;
                result.CmsNoTo = result.SourceRowIdTo;

                result.Status = SyncRunStatus.Success;
                result.CompletedUtc = DateTime.UtcNow;
                result.DurationMs = (int)stopwatch.ElapsedMilliseconds;

                await _syncRunHistoryRepository.RecordRunAsync(uow, result, cancellationToken);
                await uow.CommitAsync(cancellationToken);

                _logger.LogInformation(
                    "Sync succeeded for {JobKey}: {RecordsInserted} records, RowId {From}-{To}, {DurationMs}ms",
                    job.JobKey, result.RecordsInserted, result.SourceRowIdFrom, result.SourceRowIdTo, result.DurationMs);

                return result;
            }
            catch
            {
                await uow.RollbackAsync(cancellationToken);
                throw;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync run failed for {JobKey}", job.JobKey);
            result.Status = SyncRunStatus.Failed;
            result.ErrorMessage = ex.ToString();
            result.CompletedUtc = DateTime.UtcNow;
            result.DurationMs = (int)stopwatch.ElapsedMilliseconds;

            await TryRecordFailedRunAsync(job, result, cancellationToken);
            return result;
        }
    }

    /// <summary>
    /// Pulls every page for one job. Every page's column count is checked against
    /// job.Target.Columns.Count before it's used — a mismatch (page 1, or any later
    /// page if the source query's shape ever varies by parameter) fails the job
    /// immediately with a clear error, no partial work and no raw ImportRow schema
    /// exception.
    /// </summary>
    private async Task<DataTable> PullAllPagesAsync(SyncJobOptions job, CancellationToken cancellationToken)
    {
        DataTable? all = null;
        var cursor = 0L;

        while (true)
        {
            var page = await _sourceRepository.GetNewRecordsAsync(job.Source, cursor, job.BatchSize, job.CommandTimeoutSeconds, cancellationToken);

            if (page.Columns.Count != job.Target.Columns.Count)
            {
                throw new InvalidOperationException(
                    $"Job {job.JobKey}: source query returned {page.Columns.Count} column(s) but Target.Columns configures {job.Target.Columns.Count}. Fix the job's Target.Columns list or the source query.");
            }

            if (all is null)
            {
                all = page;
            }
            else
            {
                foreach (DataRow row in page.Rows)
                {
                    all.ImportRow(row);
                }
            }

            if (page.Rows.Count == 0)
            {
                break;
            }

            cursor = Convert.ToInt64(page.Rows[page.Rows.Count - 1][0]);

            if (page.Rows.Count < job.BatchSize)
            {
                break;
            }
        }

        return all!;
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
