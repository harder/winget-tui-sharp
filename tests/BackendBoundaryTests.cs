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

        List<int> values = BackendLimits.Materialize (
            projected,
            7,
            TestContext.Current.CancellationToken);

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
    public void ExactIdentity_PreservesBoundary_AndRejectsOversizeWithoutTruncating ()
    {
        string boundary = new ('i', BackendLimits.IdentityCharacters);
        string oversized = boundary + "x";

        Assert.Same (boundary, BackendLimits.ExactIdentity (boundary));
        Assert.Null (BackendLimits.ExactIdentity (oversized));
        Assert.True (BackendLimits.TryExactIdentity (null, out string? absent));
        Assert.Null (absent);
        Assert.False (BackendLimits.TryExactIdentity (oversized, out string? rejected));
        Assert.Null (rejected);
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

    [Fact]
    public void CharacterBudget_BoundsAggregateBelowWorstCaseFieldMultiplication ()
    {
        long naiveWorstCase = (long)BackendLimits.LocalMatches
                              * BackendLimits.SimpleTextCharacters
                              * 5;
        Assert.True (naiveWorstCase > BackendLimits.PackageResultCharacters);
        Assert.True (
            (long)BackendLimits.Versions * BackendLimits.IdentityCharacters
            > BackendLimits.VersionResultCharacters);
        Assert.True (
            (long)BackendLimits.Catalogs * BackendLimits.IdentityCharacters
            > BackendLimits.SourceResultCharacters);
        Assert.True (
            (long)BackendLimits.MetadataItems * BackendLimits.SimpleTextCharacters
            > BackendLimits.PackageDetailCharacters);
        Assert.True (
            (long)BackendLimits.VerificationItems * BackendLimits.SimpleTextCharacters * 2
            > BackendLimits.VerificationCharacters);

        CharacterBudget budget = new (BackendLimits.PackageResultCharacters);
        string maximumField = new ('x', BackendLimits.SimpleTextCharacters);
        long retained = 0;

        for (int i = 0; i < BackendLimits.LocalMatches * 5; i++)
        {
            retained += budget.TakeDisplay (maximumField, BackendLimits.SimpleTextCharacters)!.Length;
        }

        Assert.Equal (BackendLimits.PackageResultCharacters, retained);
        Assert.Equal (0, budget.Remaining);
    }

    [Fact]
    public void CharacterBudget_ExactReservationIsAtomicAtBoundary ()
    {
        CharacterBudget budget = new (5);
        CharacterBudget rejected = new (5);

        Assert.True (budget.TryReserveExact ("abc", "de"));
        Assert.Equal (0, budget.Remaining);
        Assert.False (budget.TryReserveExact ("x"));
        Assert.Equal (0, budget.Remaining);
        Assert.False (rejected.TryReserveExact ("abcd", "ef"));
        Assert.Equal (5, rejected.Remaining);
    }

    [Fact]
    public void CharacterBudget_DoesNotSplitSurrogateAtAggregateBoundary ()
    {
        CharacterBudget budget = new (4);

        string? first = budget.TakeDisplay ("abc\U0001F680", BackendLimits.SimpleTextCharacters);
        string? second = budget.TakeDisplay ("x", BackendLimits.SimpleTextCharacters);

        Assert.Equal ("abc", first);
        Assert.Equal ("x", second);
        Assert.Equal (4, first!.Length + second!.Length);
        Assert.Equal (0, budget.Remaining);
    }

    [Fact]
    public void Materialize_StopsPromptlyWhenProjectionCancels ()
    {
        using CancellationTokenSource cancellation = new ();
        CancellingProjectedList projected = new (100, cancelAfterReads: 3, cancellation);

        Assert.ThrowsAny<OperationCanceledException> (
            () => BackendLimits.Materialize (projected, 100, cancellation.Token));
        Assert.Equal (3, projected.Reads);
    }

    [Fact]
    public void Materialize_PreCancelledTokenDoesNotReadProjection ()
    {
        using CancellationTokenSource cancellation = new ();
        cancellation.Cancel ();
        CancellingProjectedList projected = new (100, cancelAfterReads: 100, cancellation);

        Assert.ThrowsAny<OperationCanceledException> (
            () => BackendLimits.Materialize (projected, 100, cancellation.Token));
        Assert.Equal (0, projected.Reads);
    }

    [Fact]
    public void BestEffortCleanup_DetachFailurePreservesPrimaryOutcomes_AndAlwaysRetains ()
    {
        int retained = 0;

        int Successful ()
        {
            try
            {
                return 42;
            }
            finally
            {
                Cleanup ();
            }
        }

        InvalidOperationException primaryFault = new ("primary");
        OperationCanceledException primaryCancellation = new ("cancelled");

        Assert.Equal (42, Successful ());
        Assert.Same (primaryFault, Record.Exception (() => ThrowWithCleanup (primaryFault)));
        Assert.Same (
            primaryCancellation,
            Record.Exception (() => ThrowWithCleanup (primaryCancellation)));
        Assert.Equal (3, retained);

        void ThrowWithCleanup (Exception primary)
        {
            try
            {
                throw primary;
            }
            finally
            {
                Cleanup ();
            }
        }

        void Cleanup ()
            => BestEffortCleanup.Run (
                () => throw new InvalidOperationException ("detach"),
                () => retained++);
    }

    [Fact]
    public void Verification_HiddenFailureAfter4095Passes_IsError ()
    {
        CollectionBudget budget = new (BackendLimits.VerificationItems);
        Assert.True (budget.TakeBounded (1).Complete);
        CollectionTake statuses = budget.TakeBounded (BackendLimits.VerificationItems);
        List<VerifyCheck> observed = Checks (statuses.Count, ok: true);

        VerificationDecision decision = VerificationEvaluator.Decide (
            [new (observed, statuses.Complete)],
            externalIncomplete: false);

        Assert.False (statuses.Complete);
        Assert.Equal (VerifyOutcome.Error, decision.Outcome);
    }

    [Fact]
    public void Verification_OmittedInstallerWithoutCompletePass_IsError ()
    {
        VerificationDecision decision = VerificationEvaluator.Decide (
            [new ([new ("Registry", false, null)], Complete: true)],
            externalIncomplete: true);

        Assert.Equal (VerifyOutcome.Error, decision.Outcome);
    }

    [Fact]
    public void Verification_CompletePassProvesOkDespiteOmittedInstaller ()
    {
        IReadOnlyList<VerifyCheck> passing = [new ("Registry", true, null)];

        VerificationDecision decision = VerificationEvaluator.Decide (
            [new (passing, Complete: true)],
            externalIncomplete: true);

        Assert.Equal (VerifyOutcome.Ok, decision.Outcome);
        Assert.Same (passing, decision.Checks);
    }

    [Fact]
    public void Verification_ExactSharedBudgetRetainsDefinitiveIssuesResult ()
    {
        CollectionBudget budget = new (BackendLimits.VerificationItems);
        Assert.True (budget.TakeBounded (1).Complete);
        CollectionTake statuses = budget.TakeBounded (BackendLimits.VerificationItems - 1);
        List<VerifyCheck> observed = Checks (statuses.Count, ok: true);
        observed [^1] = new ("Last", false, "failed");

        VerificationDecision decision = VerificationEvaluator.Decide (
            [new (observed, statuses.Complete)],
            externalIncomplete: false);

        Assert.True (statuses.Complete);
        Assert.Equal (VerifyOutcome.Issues, decision.Outcome);
        Assert.Same (observed, decision.Checks);
    }

    private static List<VerifyCheck> Checks (int count, bool ok)
    {
        List<VerifyCheck> checks = new (count);

        for (int i = 0; i < count; i++)
        {
            checks.Add (new ($"Check {i}", ok, null));
        }

        return checks;
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

    private sealed class CancellingProjectedList (
        int count,
        int cancelAfterReads,
        CancellationTokenSource cancellation) : IReadOnlyList<int>
    {
        public int Reads { get; private set; }
        public int Count { get; } = count;
        public int this [int index]
        {
            get
            {
                Reads++;

                if (Reads == cancelAfterReads)
                {
                    cancellation.Cancel ();
                }

                return index;
            }
        }

        public IEnumerator<int> GetEnumerator () => throw new InvalidOperationException ("Enumeration is forbidden.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator () => GetEnumerator ();
    }
}
