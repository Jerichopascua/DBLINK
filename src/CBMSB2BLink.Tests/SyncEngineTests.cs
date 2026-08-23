using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CBMSB2BLink.Core;
using CBMSB2BLink.Core.Abstractions;
using CBMSB2BLink.Core.Models;
using CBMSB2BLink.Core.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace CBMSB2BLink.Tests;

public class SyncEngineTests
{
    private readonly Mock<ISourceRepository> _source = new();
    private readonly Mock<IDestinationRepository> _destination = new();
    private readonly Mock<ISyncRunHistoryRepository> _syncRunHistory = new();
    private readonly Mock<ICbmsUnitOfWorkFactory> _uowFactory = new();
    private readonly Mock<ICbmsUnitOfWork> _uow = new();
    private readonly Mock<INotificationService> _notification = new();
    private readonly Mock<IRunLock> _runLock = new();
    private readonly SyncOptions _options = new() { SyncKey = "BCB_NEW", BatchSize = 10 };

    public SyncEngineTests()
    {
        _runLock.Setup(x => x.TryAcquireAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());

        _uowFactory.Setup(x => x.Create()).Returns(_uow.Object);
    }

    private SyncEngine CreateEngine() => new(
        _source.Object,
        _destination.Object,
        _syncRunHistory.Object,
        _uowFactory.Object,
        _notification.Object,
        _runLock.Object,
        Options.Create(_options),
        NullLogger<SyncEngine>.Instance);

    private static BcbRecord Record(long rowId) => new()
    {
        RowId = rowId,
        BcbCmsNo = (int)rowId,
        BcbIdNo1 = $"ID{rowId}",
        BcbName1 = $"Test Customer {rowId}",
        BcbCreateDate = DateTime.UtcNow
    };

    [Fact]
    public async Task RunAsync_HappyPath_RecordsRunAndCommits()
    {
        var batch = new List<BcbRecord> { Record(101), Record(102), Record(103) };
        _source.Setup(x => x.GetNewRecordsAsync(0, 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(batch);

        _destination.Setup(x => x.InsertBatchAsync(_uow.Object, batch, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InsertBatchResult { RecordsInserted = 3, CmsNoFrom = 501, CmsNoTo = 503 });

        var engine = CreateEngine();
        var result = await engine.RunAsync(CancellationToken.None);

        Assert.Equal(SyncRunStatus.Success, result.Status);
        Assert.Equal(3, result.RecordsInserted);
        Assert.Equal(103, result.SourceRowIdTo);
        Assert.Equal(503, result.CmsNoTo);

        _syncRunHistory.Verify(x => x.RecordRunAsync(_uow.Object, It.Is<SyncRunResult>(r => r.Status == SyncRunStatus.Success), It.IsAny<CancellationToken>()), Times.Once);
        _uow.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
        _uow.Verify(x => x.RollbackAsync(It.IsAny<CancellationToken>()), Times.Never);
        _notification.Verify(x => x.SendFailureAsync(It.IsAny<SyncRunResult>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_NoNewData_RecordsRunWithoutTouchingDestination()
    {
        _source.Setup(x => x.GetNewRecordsAsync(0, 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<BcbRecord>());

        var engine = CreateEngine();
        var result = await engine.RunAsync(CancellationToken.None);

        Assert.Equal(SyncRunStatus.NoNewData, result.Status);
        _destination.Verify(x => x.InsertBatchAsync(It.IsAny<ICbmsUnitOfWork>(), It.IsAny<IReadOnlyList<BcbRecord>>(), It.IsAny<CancellationToken>()), Times.Never);
        _syncRunHistory.Verify(x => x.RecordRunAsync(_uow.Object, It.Is<SyncRunResult>(r => r.Status == SyncRunStatus.NoNewData), It.IsAny<CancellationToken>()), Times.Once);
        _uow.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_SourceUnreachable_NotifiesAndRecordsFailure()
    {
        _source.Setup(x => x.GetNewRecordsAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("source unreachable"));

        var engine = CreateEngine();
        var result = await engine.RunAsync(CancellationToken.None);

        Assert.Equal(SyncRunStatus.Failed, result.Status);
        Assert.Contains("source unreachable", result.ErrorMessage);

        _syncRunHistory.Verify(x => x.RecordFailedRunAsync(It.Is<SyncRunResult>(r => r.Status == SyncRunStatus.Failed), It.IsAny<CancellationToken>()), Times.Once);
        _notification.Verify(x => x.SendFailureAsync(It.IsAny<SyncRunResult>(), It.IsAny<CancellationToken>()), Times.Once);
        _uow.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_DestinationInsertFails_RollsBackAndRecordsFailure()
    {
        var batch = new List<BcbRecord> { Record(101) };
        _source.Setup(x => x.GetNewRecordsAsync(0, 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(batch);

        _destination.Setup(x => x.InsertBatchAsync(_uow.Object, batch, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("insert failed"));

        var engine = CreateEngine();
        var result = await engine.RunAsync(CancellationToken.None);

        Assert.Equal(SyncRunStatus.Failed, result.Status);

        _uow.Verify(x => x.RollbackAsync(It.IsAny<CancellationToken>()), Times.Once);
        _uow.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
        _syncRunHistory.Verify(x => x.RecordFailedRunAsync(It.IsAny<SyncRunResult>(), It.IsAny<CancellationToken>()), Times.Once);
        _notification.Verify(x => x.SendFailureAsync(It.IsAny<SyncRunResult>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_MultiPageBatch_PagesUntilPartialPage()
    {
        _options.BatchSize = 2;

        _source.Setup(x => x.GetNewRecordsAsync(0, 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<BcbRecord> { Record(1), Record(2) });
        _source.Setup(x => x.GetNewRecordsAsync(2, 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<BcbRecord> { Record(3) });

        _destination.Setup(x => x.InsertBatchAsync(_uow.Object, It.Is<IReadOnlyList<BcbRecord>>(b => b.Count == 3), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InsertBatchResult { RecordsInserted = 3, CmsNoFrom = 1, CmsNoTo = 3 });

        var engine = CreateEngine();
        var result = await engine.RunAsync(CancellationToken.None);

        Assert.Equal(SyncRunStatus.Success, result.Status);
        Assert.Equal(3, result.RecordsRead);
        _source.Verify(x => x.GetNewRecordsAsync(0, 2, It.IsAny<CancellationToken>()), Times.Once);
        _source.Verify(x => x.GetNewRecordsAsync(2, 2, It.IsAny<CancellationToken>()), Times.Once);
        _source.Verify(x => x.GetNewRecordsAsync(3, 2, It.IsAny<CancellationToken>()), Times.Never);
    }
}
