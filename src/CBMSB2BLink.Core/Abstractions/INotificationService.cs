using System.Threading;
using System.Threading.Tasks;
using CBMSB2BLink.Core.Models;

namespace CBMSB2BLink.Core.Abstractions;

public interface INotificationService
{
    Task SendFailureAsync(SyncRunResult result, CancellationToken cancellationToken);
}
