namespace WingetTuiSharp.Tests;

public sealed class DetailPanelResourceTests
{
    [Fact]
    public void RemoveAndDisposeDynamicView_DisposesAfterSuccessfulRemoval ()
    {
        DisposableProbe probe = new ();
        bool removed = false;

        DetailPanel.RemoveAndDisposeDynamicView (probe, () => removed = true);

        Assert.True (removed);
        Assert.Equal (1, probe.DisposeCount);
    }

    [Fact]
    public void RemoveAndDisposeDynamicView_DisposesWhenRemovalThrows ()
    {
        DisposableProbe probe = new ();

        InvalidOperationException error = Assert.Throws<InvalidOperationException> (
            () => DetailPanel.RemoveAndDisposeDynamicView (
                probe,
                () => throw new InvalidOperationException ("remove failed")));

        Assert.Equal ("remove failed", error.Message);
        Assert.Equal (1, probe.DisposeCount);
    }

    private sealed class DisposableProbe : IDisposable
    {
        internal int DisposeCount { get; private set; }

        public void Dispose () => DisposeCount++;
    }
}
