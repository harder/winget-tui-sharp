namespace WingetTuiSharp.Tests;

public sealed class AppUiDispatchTests
{
    [Fact]
    public void QueuedCallbackIsRejectedWhenGenerationChangesBeforeExecution ()
    {
        int generation = 7;
        Func<bool> isCurrent = () => generation == 7;

        Assert.True (App.UiCallbackCanRun (true, CancellationToken.None, CancellationToken.None, isCurrent));

        generation++;

        Assert.False (App.UiCallbackCanRun (true, CancellationToken.None, CancellationToken.None, isCurrent));
    }

    [Fact]
    public void WorkerPrecheckDoesNotEvaluateUiCurrentPredicate ()
    {
        int predicateCalls = 0;
        Func<bool> uiCurrent = () =>
                               {
                                   predicateCalls++;

                                   return true;
                               };

        Assert.True (App.UiCallbackCanQueue (true, CancellationToken.None, CancellationToken.None));
        Assert.Equal (0, predicateCalls);
        Assert.True (App.UiCallbackCanRun (true, CancellationToken.None, CancellationToken.None, uiCurrent));
        Assert.Equal (1, predicateCalls);
    }

    [Fact]
    public void QueuedCallbackIsRejectedAfterRequestOrLifetimeCancellation ()
    {
        using CancellationTokenSource lifetime = new ();
        using CancellationTokenSource request = new ();
        Assert.True (App.UiCallbackCanRun (true, lifetime.Token, request.Token));

        request.Cancel ();
        Assert.False (App.UiCallbackCanRun (true, lifetime.Token, request.Token));

        using CancellationTokenSource otherRequest = new ();
        lifetime.Cancel ();
        Assert.False (App.UiCallbackCanRun (true, lifetime.Token, otherRequest.Token));
        Assert.False (App.UiCallbackCanRun (false, CancellationToken.None, CancellationToken.None));
    }

    [Fact]
    public async Task CancelledPreflightCleanupClearsOnlyItsOwnActivityAfterIgnoredCancellation ()
    {
        AppState state = new (new MockBackend ());
        using CancellationTokenSource oldRequest = new ();
        using CancellationTokenSource newerRequest = new ();
        const string activity = "Loading versions…";
        object? currentRequest = oldRequest;
        string status = activity;
        IDisposable oldLoading = state.AcquireLoading ();
        TaskCompletionSource releaseIgnoredFetch = new (TaskCreationOptions.RunContinuationsAsynchronously);
        Task ignoredCancellationFetch = Task.Run (
            async () => await releaseIgnoredFetch.Task,
            TestContext.Current.CancellationToken);

        oldRequest.Cancel ();
        releaseIgnoredFetch.SetResult ();
        await ignoredCancellationFetch.WaitAsync (TimeSpan.FromSeconds (2), TestContext.Current.CancellationToken);

        Assert.True (App.PreflightIdentityMatches (currentRequest, oldRequest));
        oldLoading.Dispose ();
        if (App.PreflightOwnsActivity (status, activity))
        {
            status = string.Empty;
        }

        Assert.False (state.Loading);
        Assert.Equal (string.Empty, status);

        using CancellationTokenSource supersededRequest = new ();
        IDisposable supersededLoading = state.AcquireLoading ();
        TaskCompletionSource releaseSupersededFetch = new (TaskCreationOptions.RunContinuationsAsynchronously);
        Task supersededFetch = Task.Run (
            async () => await releaseSupersededFetch.Task,
            TestContext.Current.CancellationToken);
        supersededRequest.Cancel ();

        IDisposable newerLoading = state.AcquireLoading ();
        currentRequest = newerRequest;
        status = "Loading newer request…";
        releaseSupersededFetch.SetResult ();
        await supersededFetch.WaitAsync (TimeSpan.FromSeconds (2), TestContext.Current.CancellationToken);

        Assert.False (App.PreflightIdentityMatches (currentRequest, supersededRequest));
        Assert.False (App.PreflightOwnsActivity (status, activity));
        supersededLoading.Dispose ();
        Assert.True (state.Loading);
        Assert.Equal ("Loading newer request…", status);

        newerLoading.Dispose ();
    }
}
