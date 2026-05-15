using System;
using System.Threading;

namespace QTSW2.Robot.Core.Execution;

public sealed partial class MismatchEscalationCoordinator
{
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (Volatile.Read(ref _auditCallbackThreadId) == Thread.CurrentThread.ManagedThreadId)
        {
            _auditTimer.Dispose();
            return;
        }

        try
        {
            using var disposed = new ManualResetEvent(false);
            if (_auditTimer.Dispose(disposed))
                disposed.WaitOne(TimeSpan.FromMilliseconds(500));
        }
        catch
        {
            try { _auditTimer.Dispose(); } catch { /* shutdown best effort */ }
        }
    }
}
