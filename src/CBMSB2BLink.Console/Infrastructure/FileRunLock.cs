using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CBMSB2BLink.Core.Abstractions;
using CBMSB2BLink.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CBMSB2BLink.App.Infrastructure;

/// <summary>
/// File-lock based IRunLock. Holds an exclusive FileStream for the process lifetime;
/// releasing (disposing) the handle is what frees the lock, including on an unclean exit.
/// </summary>
public sealed class FileRunLock : IRunLock
{
    private readonly string _lockFilePath;
    private readonly ILogger<FileRunLock> _logger;

    public FileRunLock(IOptions<SyncOptions> syncOptions, ILogger<FileRunLock> logger)
    {
        _lockFilePath = string.IsNullOrWhiteSpace(syncOptions.Value.LockFilePath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CBMSB2BLink", "run.lock")
            : syncOptions.Value.LockFilePath;
        _logger = logger;
    }

    public Task<IDisposable?> TryAcquireAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_lockFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        try
        {
            var stream = new FileStream(_lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return Task.FromResult<IDisposable?>(stream);
        }
        catch (IOException)
        {
            _logger.LogWarning("Lock file {LockFilePath} is already held by another process.", _lockFilePath);
            return Task.FromResult<IDisposable?>(null);
        }
    }
}
