namespace WingetTuiSharp.Tests;

public sealed class BackgroundTaskTrackerTests
{
    [Fact]
    public async Task AdmissionIsRejectedAfterStop ()
    {
        using BackgroundTaskTracker tracker = new ();
        tracker.BeginStop ();

        Assert.False (tracker.TryRun (_ => Task.CompletedTask));
        Assert.True (await tracker.DrainAsync (TimeSpan.FromSeconds (1)));
    }

    [Fact]
    public async Task StopCancelsAndDrainsResponsiveWork ()
    {
        using BackgroundTaskTracker tracker = new ();
        TaskCompletionSource started = new (TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True (tracker.TryRun (async ct =>
                                     {
                                         started.SetResult ();
                                         await Task.Delay (Timeout.InfiniteTimeSpan, ct);
                                     }));
        await started.Task.WaitAsync (TimeSpan.FromSeconds (2), TestContext.Current.CancellationToken);

        Assert.True (await tracker.DrainAsync (TimeSpan.FromSeconds (2)));
        Assert.True (tracker.LifetimeToken.IsCancellationRequested);
        Assert.Empty (tracker.Failures);
    }

    [Fact]
    public async Task DrainWaitsForReservedTask ()
    {
        using BackgroundTaskTracker tracker = new ();
        TaskCompletionSource release = new (TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True (tracker.TryRun (_ => release.Task));

        Task<bool> drain = tracker.DrainAsync (TimeSpan.FromSeconds (2));
        Assert.False (drain.IsCompleted);
        release.SetResult ();

        Assert.True (await drain);
    }

    [Fact]
    public async Task SynchronouslyCompletingWorkCannotEscapeRegistration ()
    {
        using BackgroundTaskTracker tracker = new ();
        int completed = 0;

        for (int i = 0; i < 256; i++)
        {
            Assert.True (tracker.TryRun (_ =>
                                         {
                                             Interlocked.Increment (ref completed);

                                             return Task.CompletedTask;
                                         }));
        }

        Assert.True (await tracker.DrainAsync (TimeSpan.FromSeconds (2)));
        Assert.Equal (256, Volatile.Read (ref completed));
    }

    [Fact]
    public async Task DeadlineIsDeterministicAndLateFailureIsObserved ()
    {
        using BackgroundTaskTracker tracker = new ();
        TaskCompletionSource release = new (TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True (tracker.TryRun (async _ =>
                                     {
                                         await release.Task;
                                         throw new InvalidOperationException ("late failure");
                                     }));

        Assert.False (await tracker.DrainAsync (TimeSpan.FromMilliseconds (30)));
        release.SetResult ();

        Assert.True (await tracker.DrainAsync (TimeSpan.FromSeconds (2)));
        Assert.Single (tracker.Failures);
        Assert.Equal ("late failure", tracker.Failures [0].Message);
    }

    [Fact]
    public async Task OutstandingTaskAdmissionIsBounded ()
    {
        using BackgroundTaskTracker tracker = new (maxOutstandingTasks: 1);
        TaskCompletionSource release = new (TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True (tracker.TryRun (_ => release.Task));
        Assert.False (tracker.TryRun (_ => Task.CompletedTask));

        release.SetResult ();
        Assert.True (await tracker.DrainAsync (TimeSpan.FromSeconds (2)));
    }

    [Fact]
    public async Task RetainedFailuresAreBounded ()
    {
        using BackgroundTaskTracker tracker = new (maxOutstandingTasks: 32);

        for (int i = 0; i < 20; i++)
        {
            Assert.True (tracker.TryRun (_ => throw new InvalidOperationException ("failure")));
        }

        Assert.True (await tracker.DrainAsync (TimeSpan.FromSeconds (2)));
        Assert.Equal (16, tracker.Failures.Count);
        Assert.Equal (4, tracker.DroppedFailureCount);
    }

    [Fact]
    public async Task DisposedTrackerRetainsSafeCancelledLifetimeToken ()
    {
        BackgroundTaskTracker tracker = new ();
        CancellationToken token = tracker.LifetimeToken;

        tracker.Dispose ();

        Assert.True (tracker.LifetimeToken.IsCancellationRequested);
        Assert.True (token.IsCancellationRequested);
        Assert.False (tracker.TryRun (_ => Task.CompletedTask));
        Assert.True (await tracker.DrainAsync (TimeSpan.Zero));
    }

    [Fact]
    public void SynchronousDrainDoesNotDependOnStoppedSynchronizationContext ()
    {
        bool drained = false;
        bool disposed = false;
        int posts = 0;
        Exception? threadFailure = null;
        Thread thread = new (() =>
                             {
                                 try
                                 {
                                     SynchronizationContext.SetSynchronizationContext (new NeverPumpedContext (() => posts++));

                                     using (BackgroundTaskTracker tracker = new ())
                                     {
                                         if (!tracker.TryRun (async _ => await Task.Delay (50)))
                                         {
                                             throw new InvalidOperationException ("Delayed task was not admitted.");
                                         }

                                         drained = tracker.DrainSynchronously (TimeSpan.FromSeconds (2));
                                     }

                                     disposed = true;
                                 }
                                 catch (Exception ex)
                                 {
                                     threadFailure = ex;
                                 }
                             })
        {
            IsBackground = true
        };

        thread.Start ();

        Assert.True (thread.Join (TimeSpan.FromSeconds (3)), "Drain waited on a synchronization context that no longer pumps.");
        Assert.Null (threadFailure);
        Assert.True (drained);
        Assert.True (disposed);
        Assert.Equal (0, Volatile.Read (ref posts));
    }

    private sealed class NeverPumpedContext (Action onPost) : SynchronizationContext
    {
        public override void Post (SendOrPostCallback d, object? state) => onPost ();
    }
}
