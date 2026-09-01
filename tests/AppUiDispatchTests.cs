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
}
