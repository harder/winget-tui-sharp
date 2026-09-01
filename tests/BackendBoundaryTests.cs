using Xunit;

namespace WingetTuiSharp.Tests;

public sealed class BackendBoundaryTests
{
    [Fact]
    public async Task Gate_AllowsOnlyOneOwner_AndReleasesAfterThrow ()
    {
        BoundedAsyncGate gate = new (4);
        int active = 0;
        int maximum = 0;

        async Task Enter (bool throws)
        {
            using IDisposable lease = await gate.AcquireAsync (CancellationToken.None);
            int now = Interlocked.Increment (ref active);
            maximum = Math.Max (maximum, now);

            try
            {
                await Task.Delay (20);

                if (throws)
                {
                    throw new InvalidOperationException ();
                }
            }
            finally
            {
                Interlocked.Decrement (ref active);
            }
        }

        await Assert.ThrowsAsync<InvalidOperationException> (() => Enter (throws: true));
        await Task.WhenAll (Enter (false), Enter (false), Enter (false));

        Assert.Equal (1, maximum);
    }

    [Fact]
    public async Task Gate_CancelledWaiter_FreesQueueCapacity ()
    {
        BoundedAsyncGate gate = new (1);
        IDisposable owner = await gate.AcquireAsync (CancellationToken.None);
        using CancellationTokenSource cancelled = new ();
        Task<IDisposable> waiter = gate.AcquireAsync (cancelled.Token).AsTask ();

        cancelled.Cancel ();
        await Assert.ThrowsAnyAsync<OperationCanceledException> (() => waiter);

        using CancellationTokenSource replacementCancellation = new ();
        Task<IDisposable> replacement = gate.AcquireAsync (replacementCancellation.Token).AsTask ();
        replacementCancellation.Cancel ();
        await Assert.ThrowsAnyAsync<OperationCanceledException> (() => replacement);

        owner.Dispose ();
        IDisposable acquired = await gate.AcquireAsync (CancellationToken.None);
        acquired.Dispose ();
        acquired.Dispose ();
    }

    [Fact]
    public async Task Gate_RejectsCallsBeyondBoundedQueue ()
    {
        BoundedAsyncGate gate = new (1);
        using IDisposable owner = await gate.AcquireAsync (CancellationToken.None);
        using CancellationTokenSource waiterCancellation = new ();
        Task<IDisposable> waiter = gate.AcquireAsync (waiterCancellation.Token).AsTask ();

        await Assert.ThrowsAsync<InvalidOperationException> (() => gate.AcquireAsync (CancellationToken.None).AsTask ());

        waiterCancellation.Cancel ();
        await Assert.ThrowsAnyAsync<OperationCanceledException> (() => waiter);
    }

    [Fact]
    public void Materialize_DoesNotTrustHugeProjectedCount ()
    {
        HugeProjectedList projected = new (int.MaxValue);

        List<int> values = BackendLimits.Materialize (projected, 7);

        Assert.Equal ([0, 1, 2, 3, 4, 5, 6], values);
        Assert.Equal (7, projected.Reads);
    }

    [Fact]
    public void Truncate_DoesNotSplitSurrogatePair ()
    {
        string value = "abc\U0001F680tail";

        Assert.Equal ("abc", BackendLimits.Truncate (value, 4));
        Assert.Equal ("abc\U0001F680", BackendLimits.Truncate (value, 5));
    }

    [Fact]
    public void TextHelpers_ApplyDocumentedSimpleAndRichLimits ()
    {
        string oversized = new ('x', BackendLimits.RichTextCharacters + 1);

        Assert.Equal (BackendLimits.SimpleTextCharacters, BackendLimits.SimpleText (oversized)!.Length);
        Assert.Equal (BackendLimits.RichTextCharacters, BackendLimits.RichText (oversized)!.Length);
        Assert.Equal ("short", BackendLimits.SimpleText ("short"));
    }

    [Fact]
    public void CollectionBudget_IsSharedAcrossNestedCollections ()
    {
        CollectionBudget budget = new (8);

        Assert.Equal (3, budget.Take (3));
        Assert.Equal (5, budget.Take (100));
        Assert.Equal (0, budget.Take (1));
        Assert.Equal (0, budget.Remaining);
    }

    private sealed class HugeProjectedList (int count) : IReadOnlyList<int>
    {
        public int Reads { get; private set; }
        public int Count { get; } = count;
        public int this [int index]
        {
            get
            {
                Reads++;
                return index;
            }
        }

        public IEnumerator<int> GetEnumerator () => throw new InvalidOperationException ("Enumeration is forbidden.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator () => GetEnumerator ();
    }
}
