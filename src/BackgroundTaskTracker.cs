using System.Collections.Concurrent;

namespace WingetTuiSharp;

/// <summary>
/// Owns fire-and-forget application work so shutdown can stop admission, cancel the shared
/// lifetime, observe failures, and wait for every task that was admitted before the stop.
/// </summary>
public sealed class BackgroundTaskTracker : IDisposable
{
    private const int DefaultMaxOutstandingTasks = 256;
    private const int MaxRetainedFailures = 16;
    private readonly object _gate = new ();
    private readonly CancellationTokenSource _lifetime = new ();
    private readonly Dictionary<long, TaskCompletionSource> _reservations = [];
    private readonly ConcurrentQueue<Exception> _failures = new ();
    private readonly int _maxOutstandingTasks;
    private long _nextId;
    private int _failureCount;
    private int _droppedFailureCount;
    private bool _accepting = true;
    private bool _disposed;

    public BackgroundTaskTracker (int maxOutstandingTasks = DefaultMaxOutstandingTasks)
    {
        if (maxOutstandingTasks <= 0)
        {
            throw new ArgumentOutOfRangeException (nameof (maxOutstandingTasks));
        }

        _maxOutstandingTasks = maxOutstandingTasks;
    }

    public CancellationToken LifetimeToken => _lifetime.Token;
    public IReadOnlyList<Exception> Failures => [.. _failures];
    public int DroppedFailureCount => Volatile.Read (ref _droppedFailureCount);

    /// <summary>
    /// Atomically reserves a drain slot before scheduling <paramref name="work"/>. The reservation
    /// prevents synchronously-completing work from escaping shutdown accounting.
    /// </summary>
    public bool TryRun (Func<CancellationToken, Task> work)
    {
        ArgumentNullException.ThrowIfNull (work);

        long id;
        TaskCompletionSource completion = new (TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_gate)
        {
            if (!_accepting || _disposed || _reservations.Count >= _maxOutstandingTasks)
            {
                return false;
            }

            id = ++_nextId;
            _reservations.Add (id, completion);
        }

        _ = Task.Run (() => ExecuteAsync (id, completion, work), CancellationToken.None);

        return true;
    }

    public void BeginStop ()
    {
        lock (_gate)
        {
            if (!_accepting)
            {
                return;
            }

            _accepting = false;
        }

        try
        {
            _lifetime.Cancel ();
        }
        catch (Exception ex)
        {
            RecordFailure (ex);
        }
    }

    /// <summary>Waits up to <paramref name="timeout"/> for every admitted reservation.</summary>
    public async Task<bool> DrainAsync (TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero && timeout != System.Threading.Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException (nameof (timeout));
        }

        BeginStop ();

        Task [] pending;

        lock (_gate)
        {
            pending = _reservations.Values.Select (r => r.Task).ToArray ();
        }

        if (pending.Length == 0)
        {
            return true;
        }

        Task all = Task.WhenAll (pending);

        if (timeout == System.Threading.Timeout.InfiniteTimeSpan)
        {
            await all.ConfigureAwait (false);

            return true;
        }

        return await Task.WhenAny (all, Task.Delay (timeout)).ConfigureAwait (false) == all;
    }

    public void Dispose ()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _accepting = false;
        }

        try
        {
            _lifetime.Cancel ();
        }
        catch (Exception ex)
        {
            RecordFailure (ex);
        }
        _lifetime.Dispose ();
    }

    private async Task ExecuteAsync (
        long id,
        TaskCompletionSource completion,
        Func<CancellationToken, Task> work)
    {
        try
        {
            await work (_lifetime.Token).ConfigureAwait (false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Lifetime cancellation is the normal shutdown path.
        }
        catch (Exception ex)
        {
            RecordFailure (ex);
        }
        finally
        {
            lock (_gate)
            {
                _reservations.Remove (id);
            }

            // The public drain task never faults: failures are retained above for diagnostics,
            // so no abandoned fire-and-forget exception can become unobserved.
            completion.TrySetResult ();
        }
    }

    private void RecordFailure (Exception exception)
    {
        int count = Interlocked.Increment (ref _failureCount);

        if (count <= MaxRetainedFailures)
        {
            _failures.Enqueue (exception);
        }
        else
        {
            Interlocked.Increment (ref _droppedFailureCount);
        }
    }
}
