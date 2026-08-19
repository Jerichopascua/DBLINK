using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CBMSB2BLink.Core.Abstractions;
using CBMSB2BLink.Core.Models;
using CBMSB2BLink.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CBMSB2BLink.Core;

/// <summary>
/// Orchestrates one CCRISB2B -> CBMS sync run: acquire lock, read watermark, pull new
/// records in pages, insert + advance watermark atomically, notify on failure.
/// </summary>
public sealed class SyncEngine
{
    private readonly ISourceRepository _sourceRepository;
    private readonly IDestinationRepository _destinationRepository;
    private readonly ISyncControlRepository _syncControlRepository;
    private readonly ICbmsUnitOfWorkFactory _unitOfWorkFactory;
    private readonly INotificationService _notificationService;
    private readonly IRunLock _runLock;
    private readonly SyncOptions _options;
    private readonly ILogger<SyncEngine> _logger;

    public SyncEngine(
        ISourceRepository sourceRepository,
        IDestinationRepository destinationRepository,
        ISyncControlRepository syncControlRepository,
        ICbmsUnitOfWorkFactory unitOfWorkFactory,
        INotificationService notificationService,
        IRunLock runLock,
        IOptions<SyncOptions> options,
        ILogger<SyncEngine> logger)
    {
        _sourceRepository = sourceRepository;
        _destinationRepository = destinationRepository;
        _syncControlRepository = syncControlRepository;
        _unitOfWorkFactory = unitOfWorkFactory;
        _notificationService = notificationService;
        _runLock = runLock;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<SyncRunResult> RunAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new SyncRunResult
        {
            SyncKey = _options.SyncKey,
            StartedUtc = DateTime.UtcNow
        };

        using var heldLock = await _runLock.TryAcquireAsync(cancellationToken);
        if (heldLock is null)
        {
            _logger.LogWarning("Another CBMSB2BLink run already holds the lock. Exiting without action.");
            result.Status = SyncRunStatus.Failed;
            result.ErrorMessage = "Skipped: another run is already in progress (lock held).";
            result.CompletedUtc = DateTime.UtcNow;
            return result;
        }

        try
        {
            var watermark = await _syncControlRepository.GetWatermarkAsync(_options.SyncKey, cancellationToken);
            _logger.LogInformation("Starting sync for {SyncKey} from LastRowId={LastRowId}", _options.SyncKey, watermark.LastRowId);

            var batch = await PullAllPagesAsync(watermark.LastRowId, cancellationToken);
            result.RecordsRead = batch.Count;

            if (batch.Count == 0)
            {
                _logger.LogInformation("No new records for {SyncKey}.", _options.SyncKey);
                result.Status = SyncRunStatus.NoNewData;
                result.CompletedUtc = DateTime.UtcNow;
                result.DurationMs = (int)stopwatch.ElapsedMilliseconds;

                await using var noopUow = _unitOfWorkFactory.Create();
                await noopUow.InitializeAsync(cancellationToken);
                await _syncControlRepository.RecordRunAsync(noopUow, result, cancellationToken);
                await noopUow.CommitAsync(cancellationToken);

                return result;
            }

            result.SourceRowIdFrom = batch[0].RowId;
            result.SourceRowIdTo = batch[^1].RowId;

            await using var uow = _unitOfWorkFactory.Create();
            await uow.InitializeAsync(cancellationToken);
            try
            {
                var insertResult = await _destinationRepository.InsertBatchAsync(uow, batch, cancellationToken);
                result.RecordsInserted = insertResult.RecordsInserted;
                result.CmsNoFrom = insertResult.CmsNoFrom;
                result.CmsNoTo = insertResult.CmsNoTo;

                await _syncControlRepository.UpdateWatermarkAsync(
                    uow, _options.SyncKey, result.SourceRowIdTo.Value, result.CmsNoTo, cancellationToken);

                result.Status = SyncRunStatus.Success;
                result.CompletedUtc = DateTime.UtcNow;
                result.DurationMs = (int)stopwatch.ElapsedMilliseconds;

                await _syncControlRepository.RecordRunAsync(uow, result, cancellationToken);

                await uow.CommitAsync(cancellationToken);

                _logger.LogInformation(
                    "Sync succeeded for {SyncKey}: {RecordsInserted} records, RowId {From}-{To}, CmsNo {CmsFrom}-{CmsTo}, {DurationMs}ms",
                    _options.SyncKey, result.RecordsInserted, result.SourceRowIdFrom, result.SourceRowIdTo,
                    result.CmsNoFrom, result.CmsNoTo, result.DurationMs);

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
            _logger.LogError(ex, "Sync run failed for {SyncKey}", _options.SyncKey);
            result.Status = SyncRunStatus.Failed;
            result.ErrorMessage = ex.ToString();
            result.CompletedUtc = DateTime.UtcNow;
            result.DurationMs = (int)stopwatch.ElapsedMilliseconds;

            await TryRecordFailedRunAsync(result, cancellationToken);
            await TryNotifyFailureAsync(result, cancellationToken);

            return result;
        }
    }

    private async Task<List<BcbRecord>> PullAllPagesAsync(long lastRowId, CancellationToken cancellationToken)
    {
        var all = new List<BcbRecord>();
        var cursor = lastRowId;

        while (true)
        {
            var page = await _sourceRepository.GetNewRecordsAsync(cursor, _options.BatchSize, cancellationToken);
            if (page.Count == 0)
            {
                break;
            }

            all.AddRange(page);
            cursor = page[^1].RowId;

            if (page.Count < _options.BatchSize)
            {
                break;
            }
        }

        return all;
    }

    private async Task TryRecordFailedRunAsync(SyncRunResult result, CancellationToken cancellationToken)
    {
        try
        {
            await _syncControlRepository.RecordFailedRunAsync(result, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record failed-run history for {SyncKey}", result.SyncKey);
        }
    }

    private async Task TryNotifyFailureAsync(SyncRunResult result, CancellationToken cancellationToken)
    {
        try
        {
            await _notificationService.SendFailureAsync(result, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send failure notification for {SyncKey}", result.SyncKey);
        }
    }
}
