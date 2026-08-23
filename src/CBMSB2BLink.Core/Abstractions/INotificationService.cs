using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CBMSB2BLink.Core.Models;

namespace CBMSB2BLink.Core.Abstractions;

public interface INotificationService
{
    /// <summary>Sends one aggregate notification listing every job that failed in a run.</summary>
    Task SendFailureAsync(IReadOnlyList<SyncRunResult> failedResults, CancellationToken cancellationToken);
}
