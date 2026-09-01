namespace WingetTuiSharp;

/// <summary>
/// A single-owner asynchronous gate with a hard limit on queued callers. The queue reservation
/// happens before waiting, so overload is rejected deterministically instead of creating an
/// unbounded chain of <see cref="Task"/> waiters.
/// </summary>
internal sealed class BoundedAsyncGate
{
    private readonly SemaphoreSlim _semaphore = new (1, 1);
    private readonly int _maxQueuedWaiters;
    private int _queuedWaiters;

    internal BoundedAsyncGate (int maxQueuedWaiters)
    {
        ArgumentOutOfRangeException.ThrowIfNegative (maxQueuedWaiters);
        _maxQueuedWaiters = maxQueuedWaiters;
    }

    internal async ValueTask<IDisposable> AcquireAsync (CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested ();

        // Do not barge ahead of an already-reserved waiter. FIFO is not promised, but honoring
        // the presence of a queue prevents a stream of new callers from starving it.
        if (Volatile.Read (ref _queuedWaiters) == 0 && _semaphore.Wait (0))
        {
            return new Lease (this);
        }

        ReserveWaiter ();

        try
        {
            await _semaphore.WaitAsync (cancellationToken).ConfigureAwait (false);
            return new Lease (this);
        }
        finally
        {
            Interlocked.Decrement (ref _queuedWaiters);
        }
    }

    private void ReserveWaiter ()
    {
        while (true)
        {
            int queued = Volatile.Read (ref _queuedWaiters);

            if (queued >= _maxQueuedWaiters)
            {
                throw new InvalidOperationException ($"The operation queue is full ({_maxQueuedWaiters} waiting callers). Try again after current work finishes.");
            }

            if (Interlocked.CompareExchange (ref _queuedWaiters, queued + 1, queued) == queued)
            {
                return;
            }
        }
    }

    private void Release () => _semaphore.Release ();

    private sealed class Lease (BoundedAsyncGate owner) : IDisposable
    {
        private BoundedAsyncGate? _owner = owner;

        public void Dispose () => Interlocked.Exchange (ref _owner, null)?.Release ();
    }
}
