namespace WingetTuiSharp.Tests;

public sealed class DetailPanelResourceTests
{
    [Fact]
    public void ClearDynamicViews_DisposesViewReturnedByRemove ()
    {
        DisposableProbe probe = new ();
        List<DisposableProbe> tracked = [probe];

        DetailPanel.ClearDynamicViews (tracked, view => view);

        Assert.Empty (tracked);
        Assert.Equal (1, probe.DisposeCount);
    }

    [Fact]
    public void ClearDynamicViews_NullRemovalLeavesLifecycleWithParent ()
    {
        DisposableProbe probe = new ();
        List<DisposableProbe> tracked = [probe];

        DetailPanel.ClearDynamicViews (tracked, _ => null);

        Assert.Empty (tracked);
        Assert.Equal (0, probe.DisposeCount);
    }

    [Fact]
    public void ClearDynamicViews_ThrowLeavesLifecycleWithParentAndSurfacesError ()
    {
        DisposableProbe probe = new ();
        List<DisposableProbe> tracked = [probe];

        InvalidOperationException error = Assert.Throws<InvalidOperationException> (
            () => DetailPanel.ClearDynamicViews<DisposableProbe> (
                tracked,
                _ => throw new InvalidOperationException ("remove failed")));

        Assert.Equal ("remove failed", error.Message);
        Assert.Empty (tracked);
        Assert.Equal (0, probe.DisposeCount);
    }

    [Fact]
    public void ClearDynamicViews_ContinuesAfterFailuresAndDoesNotDoubleDispose ()
    {
        DisposableProbe removedFirst = new () { Outcome = RemovalOutcome.Returned };
        DisposableProbe threw = new () { Outcome = RemovalOutcome.Throw };
        DisposableProbe parentOwned = new () { Outcome = RemovalOutcome.Null };
        List<DisposableProbe> tracked = [removedFirst, threw, parentOwned];

        Assert.Throws<InvalidOperationException> (
            () => DetailPanel.ClearDynamicViews (
                tracked,
                probe => probe.Outcome switch
                {
                    RemovalOutcome.Returned => probe,
                    RemovalOutcome.Null => null,
                    _ => throw new InvalidOperationException ("remove failed")
                }));

        Assert.Empty (tracked);
        Assert.Equal (1, removedFirst.DisposeCount);
        Assert.Equal (0, threw.DisposeCount);
        Assert.Equal (0, parentOwned.DisposeCount);

        DetailPanel.ClearDynamicViews (tracked, probe => probe);
        Assert.Equal (1, removedFirst.DisposeCount);
    }

    private sealed class DisposableProbe : IDisposable
    {
        internal RemovalOutcome Outcome { get; init; }
        internal int DisposeCount { get; private set; }

        public void Dispose () => DisposeCount++;
    }

    private enum RemovalOutcome
    {
        Returned,
        Null,
        Throw
    }
}
