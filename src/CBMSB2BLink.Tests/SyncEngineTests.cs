using System;
using System.Collections.Generic;
using System.Data;
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
    private readonly Mock<ITargetUnitOfWorkFactory> _uowFactory = new();
    private readonly Mock<ITargetUnitOfWork> _uow = new();
    private readonly Mock<INotificationService> _notification = new();
    private readonly Mock<IRunLock> _runLock = new();
    private readonly SyncOptions _options = new() { MaxRunDurationSeconds = 60, Jobs = new List<SyncJobOptions> { Job("JOB1") } };

    public SyncEngineTests()
    {
        _runLock.Setup(x => x.TryAcquireAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());

        _uowFactory.Setup(x => x.Create(It.IsAny<string>())).Returns(_uow.Object);
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

    private static SyncJobOptions Job(string jobKey, int batchSize = 10) => new()
    {
        JobKey = jobKey,
        Source = new SourceJobOptions { ConnectionString = "source-cs", CommandText = $"usp_Test_{jobKey}", CommandType = "StoredProcedure" },
        Target = new TargetJobOptions { ConnectionString = "target-cs", Table = "dbo.Target", Columns = new List<string> { "KeyCol", "NameCol" } },
        BatchSize = batchSize,
        CommandTimeoutSeconds = 30
    };

    /// <summary>Builds a page with the correct 2-column shape matching Job()'s Target.Columns.</summary>
    private static DataTable Page(params long[] keys)
    {
        var table = new DataTable();
        table.Columns.Add("Key", typeof(long));
        table.Columns.Add("Name", typeof(string));
        foreach (var key in keys)
        {
            table.Rows.Add(key, $"Row{key}");
        }
        return table;
    }

    private void SetupSource(string commandText, DataTable page)
    {
        _source.Setup(x => x.GetNewRecordsAsync(
                It.Is<SourceJobOptions>(s => s.CommandText == commandText),
                It.IsAny<long>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(page);
    }

    [Fact]
    public async Task RunAsync_HappyPath_RecordsRunAndCommits()
    {
        SetupSource("usp_Test_JOB1", Page(101, 102, 103));
        _destination.Setup(x => x.InsertBatchAsync(_uow.Object, "dbo.Target", It.IsAny<IReadOnlyList<string>>(), It.IsAny<DataTable>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InsertBatchResult { RecordsInserted = 3 });

        var engine = CreateEngine();
        var results = await engine.RunAsync(CancellationToken.None);

        Assert.Single(results);
        Assert.Equal(SyncRunStatus.Success, results[0].Status);
        Assert.Equal(3, results[0].RecordsInserted);
        Assert.Equal(103, results[0].SourceRowIdTo);
        Assert.Equal(103, results[0].CmsNoTo);

        _syncRunHistory.Verify(x => x.RecordRunAsync(_uow.Object, It.Is<SyncRunResult>(r => r.Status == SyncRunStatus.Success), It.IsAny<CancellationToken>()), Times.Once);
        _uow.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
        _uow.Verify(x => x.RollbackAsync(It.IsAny<CancellationToken>()), Times.Never);
        _notification.Verify(x => x.SendFailureAsync(It.IsAny<IReadOnlyList<SyncRunResult>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_NoNewData_RecordsRunWithoutTouchingDestination()
    {
        SetupSource("usp_Test_JOB1", Page());

        var engine = CreateEngine();
        var results = await engine.RunAsync(CancellationToken.None);

        Assert.Equal(SyncRunStatus.NoNewData, results[0].Status);
        _destination.Verify(x => x.InsertBatchAsync(It.IsAny<ITargetUnitOfWork>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<DataTable>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _syncRunHistory.Verify(x => x.RecordRunAsync(_uow.Object, It.Is<SyncRunResult>(r => r.Status == SyncRunStatus.NoNewData), It.IsAny<CancellationToken>()), Times.Once);
        _uow.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_FieldCountMismatch_FailsJobWithoutInserting()
    {
        var wrongShapedPage = new DataTable();
        wrongShapedPage.Columns.Add("OnlyOneColumn", typeof(long));
        wrongShapedPage.Rows.Add(1L);

        SetupSource("usp_Test_JOB1", wrongShapedPage);

        var engine = CreateEngine();
        var results = await engine.RunAsync(CancellationToken.None);

        Assert.Equal(SyncRunStatus.Failed, results[0].Status);
        Assert.Contains("1 column(s)", results[0].ErrorMessage);
        Assert.Contains("configures 2", results[0].ErrorMessage);
        _destination.Verify(x => x.InsertBatchAsync(It.IsAny<ITargetUnitOfWork>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<DataTable>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_OneJobFails_OtherJobStillRuns()
    {
        _options.Jobs = new List<SyncJobOptions> { Job("JOB1"), Job("JOB2") };

        _source.Setup(x => x.GetNewRecordsAsync(
                It.Is<SourceJobOptions>(s => s.CommandText == "usp_Test_JOB1"),
                It.IsAny<long>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("source unreachable"));

        SetupSource("usp_Test_JOB2", Page(201));
        _destination.Setup(x => x.InsertBatchAsync(_uow.Object, "dbo.Target", It.IsAny<IReadOnlyList<string>>(), It.IsAny<DataTable>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InsertBatchResult { RecordsInserted = 1 });

        var engine = CreateEngine();
        var results = await engine.RunAsync(CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal(SyncRunStatus.Failed, results[0].Status);
        Assert.Equal("JOB1", results[0].SyncKey);
        Assert.Equal(SyncRunStatus.Success, results[1].Status);
        Assert.Equal("JOB2", results[1].SyncKey);

        _notification.Verify(x => x.SendFailureAsync(
            It.Is<IReadOnlyList<SyncRunResult>>(list => list.Count == 1 && list[0].SyncKey == "JOB1"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_DestinationInsertFails_RollsBackAndRecordsFailure()
    {
        SetupSource("usp_Test_JOB1", Page(101));
        _destination.Setup(x => x.InsertBatchAsync(_uow.Object, "dbo.Target", It.IsAny<IReadOnlyList<string>>(), It.IsAny<DataTable>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("insert failed"));

        var engine = CreateEngine();
        var results = await engine.RunAsync(CancellationToken.None);

        Assert.Equal(SyncRunStatus.Failed, results[0].Status);

        _uow.Verify(x => x.RollbackAsync(It.IsAny<CancellationToken>()), Times.Once);
        _uow.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
        _syncRunHistory.Verify(x => x.RecordFailedRunAsync("target-cs", It.IsAny<SyncRunResult>(), It.IsAny<CancellationToken>()), Times.Once);
        _notification.Verify(x => x.SendFailureAsync(It.IsAny<IReadOnlyList<SyncRunResult>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_MultiPageBatch_PagesUntilPartialPage()
    {
        _options.Jobs = new List<SyncJobOptions> { Job("JOB1", batchSize: 2) };

        _source.SetupSequence(x => x.GetNewRecordsAsync(
                It.Is<SourceJobOptions>(s => s.CommandText == "usp_Test_JOB1"),
                It.IsAny<long>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page(1, 2))
            .ReturnsAsync(Page(3));

        _destination.Setup(x => x.InsertBatchAsync(_uow.Object, "dbo.Target", It.IsAny<IReadOnlyList<string>>(), It.Is<DataTable>(t => t.Rows.Count == 3), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InsertBatchResult { RecordsInserted = 3 });

        var engine = CreateEngine();
        var results = await engine.RunAsync(CancellationToken.None);

        Assert.Equal(SyncRunStatus.Success, results[0].Status);
        Assert.Equal(3, results[0].RecordsRead);
        _source.Verify(x => x.GetNewRecordsAsync(It.IsAny<SourceJobOptions>(), 0, 2, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        _source.Verify(x => x.GetNewRecordsAsync(It.IsAny<SourceJobOptions>(), 2, 2, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        _source.Verify(x => x.GetNewRecordsAsync(It.IsAny<SourceJobOptions>(), 3, 2, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_LockHeld_ReturnsFailedWithoutRunningAnyJobAndNotifies()
    {
        _runLock.Setup(x => x.TryAcquireAsync(It.IsAny<CancellationToken>())).ReturnsAsync((IDisposable?)null);

        var engine = CreateEngine();
        var results = await engine.RunAsync(CancellationToken.None);

        Assert.Single(results);
        Assert.Equal(SyncRunStatus.Failed, results[0].Status);
        _source.Verify(x => x.GetNewRecordsAsync(It.IsAny<SourceJobOptions>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        // Lock contention is a real operational signal (a stuck/overlapping run) and now
        // notifies like any other job failure, instead of failing silently.
        _notification.Verify(x => x.SendFailureAsync(
            It.Is<IReadOnlyList<SyncRunResult>>(list => list.Count == 1 && list[0].SyncKey == "(lock)"),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
