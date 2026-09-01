namespace WingetTuiSharp.Tests;

public sealed class AppStateLoadingTests
{
    [Fact]
    public void OverlappingOwnersReleaseOnlyTheirOwnLease ()
    {
        AppState state = new (new MockBackend ());
        IDisposable first = state.AcquireLoading ();
        IDisposable second = state.AcquireLoading ();

        Assert.True (state.Loading);
        first.Dispose ();
        Assert.True (state.Loading);
        second.Dispose ();
        Assert.False (state.Loading);
    }

    [Fact]
    public void ReleaseIsIdempotentAndDetailOwnershipIsIndependent ()
    {
        AppState state = new (new MockBackend ());
        IDisposable general = state.AcquireLoading ();
        IDisposable detail = state.AcquireLoading (detail: true);

        general.Dispose ();
        general.Dispose ();
        Assert.False (state.Loading);
        Assert.True (state.DetailLoading);

        detail.Dispose ();
        detail.Dispose ();
        Assert.False (state.DetailLoading);
    }
}
