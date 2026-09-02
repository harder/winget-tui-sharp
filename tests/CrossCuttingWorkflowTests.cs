using System.Collections;
using System.Diagnostics;

namespace WingetTuiSharp.Tests;

public sealed class CrossCuttingWorkflowTests
{
    [Fact]
    public void StatusOwnership_DefersLatestAmbientErrorAndPublishesItWithOutcome ()
    {
        StatusOwnership ownership = new ();
        string status = "Installing package · Esc to cancel";
        bool isError = false;
        Assert.True (ownership.BeginOperation (operationId: 7));
        string [] ambientSuccesses =
        [
            "Loading Installed…",
            "12 packages",
            "Press / to search for packages",
            "Opened https://example.invalid/",
            "Exporting 12 rows…",
            "Exported 12 rows"
        ];

        foreach (string message in ambientSuccesses)
        {
            bool written = ownership.TryWrite (StatusOwner.Ambient, message, isError: false, Write);
            Assert.False (written);
            Assert.Equal ("Installing package · Esc to cancel", status);
            Assert.False (isError);
        }

        Assert.False (ownership.TryWrite (StatusOwner.Ambient, "List error", isError: true, Write));
        Assert.False (ownership.TryWrite (StatusOwner.Ambient, "Detail error", isError: true, Write));
        Assert.False (ownership.TryWrite (StatusOwner.Ambient, "Background admission error", isError: true, Write));
        Assert.False (ownership.TryWrite (StatusOwner.Ambient, "Open failed", isError: true, Write));
        Assert.Equal (1, ownership.DeferredErrorCount);

        Assert.True (ownership.CompleteOperation (7, "Done", outcomeIsError: false, Write));
        Assert.Contains ("Done", status, StringComparison.Ordinal);
        Assert.Contains ("Open failed", status, StringComparison.Ordinal);
        Assert.True (isError);
        Assert.Equal (0, ownership.DeferredErrorCount);

        Assert.True (ownership.TryWrite (StatusOwner.Ambient, "12 packages", isError: false, Write));
        Assert.Equal ("12 packages", status);
        Assert.False (isError);

        void Write (string message, bool error)
        {
            status = message;
            isError = error;
        }
    }

    [Fact]
    public void StatusOwnership_BoundsOneDeferredSlotScalarSafelyAndClearDropsIt ()
    {
        StatusOwnership ownership = new ();
        Assert.True (ownership.BeginOperation (1));
        string oversized = new string ('x', StatusOwnership.MaxDeferredErrorCharacters - 1) + "😀tail";

        for (int i = 0; i < 1000; i++)
        {
            ownership.TryWrite (StatusOwner.Ambient, oversized + i, isError: true, (_, _) => { });
        }

        Assert.Equal (1, ownership.DeferredErrorCount);
        string published = string.Empty;
        ownership.CompleteOperation (1, "Done", false, (message, _) => published = message);
        Assert.DoesNotContain ('\uD83D', published);
        Assert.DoesNotContain ('\uDE00', published);
        Assert.InRange (published.Length, 1, StatusOwnership.MaxPublishedStatusCharacters);

        Assert.True (ownership.BeginOperation (2));
        ownership.TryWrite (StatusOwner.Ambient, "shutdown error", true, (_, _) => { });
        string longOutcome = new string ('o', StatusOwnership.MaxPublishedStatusCharacters * 2);
        ownership.CompleteOperation (2, longOutcome, false, (message, _) => published = message);
        Assert.Contains ("Background error: shutdown error", published, StringComparison.Ordinal);
        Assert.StartsWith ("ooo", published, StringComparison.Ordinal);
        Assert.InRange (published.Length, 1, StatusOwnership.MaxPublishedStatusCharacters);

        Assert.True (ownership.BeginOperation (3));
        Assert.False (ownership.CompleteOperation (2, "older result", true, (_, _) => Assert.Fail ("older write")));
        ownership.Clear ();
        Assert.Equal (0, ownership.DeferredErrorCount);
        Assert.False (ownership.CompleteOperation (3, "stale", true, (_, _) => Assert.Fail ("stale write")));
    }

    public static TheoryData<int, int> ForegroundAdmissionPairs => new ()
    {
        { (int) ForegroundWorkflow.Operation, (int) ForegroundWorkflow.Operation },
        { (int) ForegroundWorkflow.Operation, (int) ForegroundWorkflow.Preflight },
        { (int) ForegroundWorkflow.Operation, (int) ForegroundWorkflow.Export },
        { (int) ForegroundWorkflow.Preflight, (int) ForegroundWorkflow.Operation },
        { (int) ForegroundWorkflow.Preflight, (int) ForegroundWorkflow.Preflight },
        { (int) ForegroundWorkflow.Preflight, (int) ForegroundWorkflow.Export },
        { (int) ForegroundWorkflow.Export, (int) ForegroundWorkflow.Operation },
        { (int) ForegroundWorkflow.Export, (int) ForegroundWorkflow.Preflight },
        { (int) ForegroundWorkflow.Export, (int) ForegroundWorkflow.Export }
    };

    [Theory]
    [MemberData (nameof (ForegroundAdmissionPairs))]
    public void ForegroundWorkflowCoordinator_AllWorkflowPairsAreMutuallyExclusive (
        int firstValue,
        int secondValue)
    {
        ForegroundWorkflow first = (ForegroundWorkflow) firstValue;
        ForegroundWorkflow second = (ForegroundWorkflow) secondValue;
        ForegroundWorkflowCoordinator coordinator = new ();
        Assert.True (coordinator.TryBegin (first, out ForegroundAdmission admission));
        Assert.False (coordinator.TryBegin (second, out _));
        Assert.True (coordinator.Release (admission));
        Assert.False (coordinator.Release (admission));
        Assert.True (coordinator.TryBegin (second, out _));
    }

    [Fact]
    public void CompletePreflight_ReleasesBeforeOperationAndReportsThrowingResult ()
    {
        ForegroundWorkflowCoordinator coordinator = new ();
        Assert.True (coordinator.TryBegin (ForegroundWorkflow.Preflight, out ForegroundAdmission preflight));
        bool loading = true;
        string? visibleError = null;

        App.CompletePreflight (
            () =>
            {
                loading = false;
                Assert.True (coordinator.Release (preflight));
            },
            () =>
            {
                Assert.False (loading);
                Assert.True (coordinator.TryBegin (ForegroundWorkflow.Operation, out _));
                throw new InvalidOperationException ("modal failed");
            },
            ex => visibleError = ex.Message);

        Assert.Equal ("modal failed", visibleError);
    }

    [Fact]
    public void PreflightCancellationIsOwnedByApplicationLifetimeNotDetailRefresh ()
    {
        using CancellationTokenSource lifetime = new ();
        using CancellationTokenSource detailRefresh = new ();
        using CancellationTokenSource request = App.CreatePreflightSource (lifetime.Token);

        detailRefresh.Cancel ();
        Assert.False (request.IsCancellationRequested);

        lifetime.Cancel ();
        Assert.True (request.IsCancellationRequested);
    }

    [Fact]
    public async Task PreflightCleanup_ReleasesAndDisposesWhenUiDispatchThrows ()
    {
        ForegroundWorkflowCoordinator coordinator = new ();
        Assert.True (coordinator.TryBegin (ForegroundWorkflow.Preflight, out ForegroundAdmission admission));
        int releaseCount = 0;
        int disposeCount = 0;

        await Assert.ThrowsAsync<InvalidOperationException> (
            () => App.CleanupPreflightAsync (
                () => throw new InvalidOperationException ("dispatch failed"),
                () =>
                {
                    releaseCount++;
                    Assert.True (coordinator.Release (admission));
                },
                () => disposeCount++));

        Assert.Equal (1, releaseCount);
        Assert.Equal (1, disposeCount);
        Assert.True (coordinator.TryBegin (ForegroundWorkflow.Operation, out _));
    }

    [Fact]
    public void OperationReservation_BusySkipsConfirmationCancelReleasesAndTransferIsOnce ()
    {
        foreach (ForegroundWorkflow blocker in Enum.GetValues<ForegroundWorkflow> ())
        {
            ForegroundWorkflowCoordinator busy = new ();
            Assert.True (busy.TryBegin (blocker, out ForegroundAdmission active));
            bool confirmInvoked = false;
            Assert.False (App.TryUseOperationReservation (
                busy,
                _ =>
                {
                    confirmInvoked = true;

                    return true;
                }));
            Assert.False (confirmInvoked);
            Assert.True (busy.Release (active));
        }

        ForegroundWorkflowCoordinator coordinator = new ();
        Assert.False (App.TryUseOperationReservation (coordinator, _ => false));
        Assert.True (coordinator.TryReserveOperation (out OperationReservation? afterCancel));
        afterCancel!.Dispose ();

        Assert.Throws<InvalidOperationException> (
            () => App.TryUseOperationReservation (
                coordinator,
                _ => throw new InvalidOperationException ("modal failed")));
        Assert.True (coordinator.TryReserveOperation (out OperationReservation? afterException));
        afterException!.Dispose ();

        ForegroundAdmission transferred = default;
        Assert.True (App.TryUseOperationReservation (
            coordinator,
            reservation =>
            {
                Assert.True (reservation.TryTransfer (out transferred));
                Assert.False (reservation.TryTransfer (out _));

                return true;
            }));
        Assert.False (coordinator.TryBegin (ForegroundWorkflow.Export, out _));
        Assert.True (coordinator.Release (transferred));
    }

    [Fact]
    public void PreflightHandoff_ReservesOperationBeforeModalCallback ()
    {
        ForegroundWorkflowCoordinator coordinator = new ();
        Assert.True (coordinator.TryBegin (ForegroundWorkflow.Preflight, out ForegroundAdmission preflight));
        Assert.True (coordinator.Release (preflight));
        bool modalInvoked = false;

        Assert.False (App.TryUseOperationReservation (
            coordinator,
            reservation =>
            {
                modalInvoked = true;
                Assert.False (coordinator.TryBegin (ForegroundWorkflow.Export, out _));
                Assert.NotNull (reservation);

                return false;
            }));
        Assert.True (modalInvoked);
        Assert.True (coordinator.TryBegin (ForegroundWorkflow.Export, out _));
    }

    [Fact]
    public void ReservedInformationalModal_DoesNotOverlapAndAlwaysReleases ()
    {
        ForegroundWorkflowCoordinator coordinator = new ();
        Assert.True (coordinator.TryBegin (ForegroundWorkflow.Preflight, out ForegroundAdmission preflight));
        bool shown = false;
        Assert.False (App.TryShowReservedModal (coordinator, () => shown = true));
        Assert.False (shown);
        Assert.True (coordinator.Release (preflight));

        Assert.True (App.TryShowReservedModal (
            coordinator,
            () =>
            {
                shown = true;
                Assert.False (coordinator.TryBegin (ForegroundWorkflow.Export, out _));
            }));
        Assert.True (shown);
        Assert.True (coordinator.TryBegin (ForegroundWorkflow.Export, out ForegroundAdmission afterReturn));
        Assert.True (coordinator.Release (afterReturn));

        Assert.Throws<InvalidOperationException> (
            () => App.TryShowReservedModal (
                coordinator,
                () => throw new InvalidOperationException ("verify modal failed")));
        Assert.True (coordinator.TryBegin (ForegroundWorkflow.Operation, out ForegroundAdmission afterThrow));
        Assert.True (coordinator.Release (afterThrow));
    }

    [Fact]
    public void PinSnapshot_ApplyIsCaseInsensitiveAndClearsAbsentPins ()
    {
        Package pinned = new () { Id = "Vendor.Package", Name = "Package", PinState = PinState.Unpinned };
        Package removed = new () { Id = "Vendor.Removed", Name = "Removed", PinState = new (PinStateKind.Blocking) };
        Dictionary<string, PinState> snapshot = new ()
        {
            ["vendor.package"] = new (PinStateKind.Gating, "2.0")
        };

        AppState state = new (new MockBackend ());
        Assert.True (state.RecordPinSnapshot (snapshot));
        Assert.True (state.ApplyPinSnapshot ([pinned, removed]));

        Assert.Equal (PinStateKind.Gating, pinned.PinState.Kind);
        Assert.Equal ("2.0", pinned.PinState.GatingVersion);
        Assert.Equal (PinState.Unpinned, removed.PinState);
    }

    [Fact]
    public void PinSnapshot_FirstFailurePreservesRowsAndDisablesPinDecisions ()
    {
        AppState state = new (new MockBackend ());
        Package backendPinned = new ()
        {
            Id = "Vendor.Pinned",
            Name = "Pinned",
            PinState = new (PinStateKind.Blocking)
        };

        state.MarkPinsStale ();

        Assert.True (backendPinned.PinState.IsPinned);
        Assert.False (state.PinDataFresh);
        Assert.False (state.HasPinSnapshot);
        Assert.False (state.CyclePinFilter ());
    }

    [Fact]
    public void PinSnapshot_SuccessFailureRecoveryAndUnpinClear ()
    {
        AppState state = new (new MockBackend ());
        Assert.True (state.RecordPinSnapshot (new Dictionary<string, PinState>
        {
            ["Vendor.Pinned"] = new (PinStateKind.Blocking)
        }));
        Assert.True (state.PinDataFresh);
        Assert.Equal (1, state.PinSnapshotCount);

        state.MarkPinsStale ();
        Assert.False (state.PinDataFresh);
        Assert.True (state.HasPinSnapshot);
        Assert.Equal (1, state.PinSnapshotCount);

        Assert.True (state.RecordPinSnapshot (new Dictionary<string, PinState> ()));
        Assert.True (state.PinDataFresh);
        Assert.True (state.HasPinSnapshot);
        Assert.Equal (0, state.PinSnapshotCount);
    }

    [Fact]
    public void PinSnapshot_IncompleteSourceKeepsLastKnownAndWarningDefersUnderOperation ()
    {
        AppState state = new (new MockBackend ());
        Assert.True (state.RecordPinSnapshot (new Dictionary<string, PinState>
        {
            ["Last.Known"] = new (PinStateKind.Blocking)
        }));
        CountingPinDictionary pins = new (
            BoundedPinSnapshot.MaxEntries + 100,
            Enumerable.Range (0, BoundedPinSnapshot.MaxEntries + 100)
                      .Select (value => Pair ($"Package.{value:D6}")));
        Assert.False (state.RecordPinSnapshot (pins));
        Assert.Equal (0, pins.EnumeratorRequests);
        Assert.False (state.PinDataFresh);
        Assert.Equal (1, state.PinSnapshotCount);

        StatusOwnership status = new ();
        Assert.True (status.BeginOperation (42));
        Assert.False (status.TryWrite (
            StatusOwner.Ambient,
            "Pin status unavailable; pin actions and filtering are disabled",
            isError: true,
            (_, _) => Assert.Fail ("warning must defer")));
        string published = string.Empty;
        status.CompleteOperation (42, "Done", false, (message, _) => published = message);
        Assert.Contains ("Pin status unavailable", published, StringComparison.Ordinal);
    }

    [Fact]
    public void PinSnapshot_StreamingBoundsEnumerationKeysVersionsAndAggregateBudget ()
    {
        BoundedPinSnapshot snapshot = new ();
        Assert.True (snapshot.TryRecord (new Dictionary<string, PinState>
        {
            ["Exact.Key"] = new (PinStateKind.Gating, new string ('v', 300) + "😀tail")
        }));
        Assert.True (snapshot.TryGet ("exact.key", out PinState retained));
        Assert.InRange (retained.GatingVersion!.Length, 1, BoundedPinSnapshot.MaxGatingVersionCharacters);
        Assert.False (char.IsHighSurrogate (retained.GatingVersion [^1]));

        CountingPinDictionary tooManyDespiteCount = new (
            reportedCount: 1,
            Enumerable.Range (0, BoundedPinSnapshot.MaxEntries + 100)
                      .Select (value => Pair ($"Overflow.{value}")));
        Assert.False (snapshot.TryRecord (tooManyDespiteCount));
        Assert.InRange (tooManyDespiteCount.MoveNextCount, 1, BoundedPinSnapshot.MaxEntries + 1);
        Assert.False (snapshot.IsFresh);
        Assert.Equal (1, snapshot.Count);

        string hugeId = new string ('k', BoundedPinSnapshot.MaxKeyCharacters + 1);
        Assert.False (snapshot.TryRecord (new Dictionary<string, PinState>
        {
            [hugeId] = new (PinStateKind.Blocking)
        }));
        Assert.Equal (1, snapshot.Count);

        CountingPinDictionary aggregateFlood = new (
            reportedCount: 100,
            Enumerable.Range (0, 100)
                      .Select (value => Pair (new string ((char) ('a' + value % 20), BoundedPinSnapshot.MaxKeyCharacters))));
        Assert.False (snapshot.TryRecord (aggregateFlood));
        Assert.InRange (
            aggregateFlood.MoveNextCount,
            1,
            BoundedPinSnapshot.MaxAggregateCharacters / BoundedPinSnapshot.MaxKeyCharacters + 2);
    }

    [Fact]
    public void PinSnapshot_RejectsSourceThatEnumeratesFewerEntriesThanItReports ()
    {
        BoundedPinSnapshot snapshot = new ();
        Assert.True (snapshot.TryRecord (new Dictionary<string, PinState>
        {
            ["Vendor.Pinned"] = new (PinStateKind.Blocking)
        }));

        // Count says five, the enumerator yields two. Accepting that as complete would report the
        // three unlisted packages as unpinned, which is a silent wrong answer rather than a gap.
        CountingPinDictionary underReporting = new (
            reportedCount: 5,
            [Pair ("Vendor.One"), Pair ("Vendor.Two")]);

        Assert.False (snapshot.TryRecord (underReporting));
        Assert.False (snapshot.IsFresh);

        // The previously accepted snapshot is retained (stale-marked), never replaced by partial data.
        Assert.True (snapshot.HasSnapshot);
        Assert.Equal (1, snapshot.Count);
    }

    [Fact]
    public void PinSnapshot_RejectsOverEnumerationAtTheFirstExtraEntry ()
    {
        BoundedPinSnapshot snapshot = new ();
        CountingPinDictionary overReporting = new (
            reportedCount: 2,
            [Pair ("Vendor.One"), Pair ("Vendor.Two"), Pair ("Vendor.Three")]);

        Assert.False (snapshot.TryRecord (overReporting));
        Assert.False (snapshot.IsFresh);

        // Bailing at the first entry past Count means an endless enumerator costs three MoveNexts,
        // not MaxEntries of them.
        Assert.Equal (3, overReporting.MoveNextCount);
    }

    [Fact]
    public void PinSnapshot_RejectsCaseInsensitiveDuplicateIdsAndPreservesLastKnownState ()
    {
        BoundedPinSnapshot snapshot = new ();
        Assert.True (snapshot.TryRecord (new Dictionary<string, PinState>
        {
            ["Vendor.Known"] = new (PinStateKind.Blocking)
        }));
        Dictionary<string, PinState> conflicting = new (StringComparer.Ordinal)
        {
            ["Vendor.Package"] = new (PinStateKind.Blocking),
            ["vendor.package"] = new (PinStateKind.Gating, "2.*")
        };

        Assert.False (snapshot.TryRecord (conflicting));
        Assert.False (snapshot.IsFresh);
        Assert.True (snapshot.HasSnapshot);
        Assert.Equal (1, snapshot.Count);
    }

    [Fact]
    public void CachedDetail_AdoptsFreshSnapshotPinIncludingAnExternalUnpin ()
    {
        AppState state = new (new MockBackend ());
        Assert.True (state.RecordPinSnapshot (new Dictionary<string, PinState>
        {
            ["Vendor.Package"] = new (PinStateKind.Blocking)
        }));

        Package pinned = new () { Id = "Vendor.Package", Name = "Package", PinState = new (PinStateKind.Blocking) };
        Assert.True (state.CacheDetail (pinned.Id, new () { Id = pinned.Id, Name = pinned.Name, PinState = pinned.PinState }));
        Assert.True (state.TryGetCachedDetail (pinned, out PackageDetail whilePinned));
        Assert.True (whilePinned.PinState.IsPinned);

        // Somebody ran `winget pin remove` outside the app; the next refresh records that.
        Assert.True (state.RecordPinSnapshot (new Dictionary<string, PinState> ()));
        Package unpinned = new () { Id = "Vendor.Package", Name = "Package" };

        Assert.True (state.TryGetCachedDetail (unpinned, out PackageDetail afterExternalUnpin));
        Assert.Equal (PinState.Unpinned, afterExternalUnpin.PinState);

        // …and the correction is written back, not recomputed on every read from a stale entry.
        Assert.True (state.TryGetCachedDetail (unpinned, out PackageDetail reread));
        Assert.Equal (PinState.Unpinned, reread.PinState);
    }

    [Fact]
    public void CachedDetail_KeepsListRowPinWhenSnapshotIsStale ()
    {
        AppState state = new (new MockBackend ());
        Package pinned = new () { Id = "Vendor.Package", Name = "Package", PinState = new (PinStateKind.Blocking) };
        Assert.True (state.CacheDetail (pinned.Id, new () { Id = pinned.Id, Name = pinned.Name }));

        // With no fresh pin authority, "unknown" must not be downgraded to "unpinned".
        state.MarkPinsStale ();
        Assert.True (state.TryGetCachedDetail (pinned, out PackageDetail detail));
        Assert.True (detail.PinState.IsPinned);
    }

    [Fact]
    public void PinSnapshot_AppliesRetainedCopyAndCompleteEmptySnapshotClearsUnpinned ()
    {
        AppState state = new (new MockBackend ());
        Dictionary<string, PinState> source = new (StringComparer.OrdinalIgnoreCase)
        {
            ["Vendor.Package"] = new (PinStateKind.Blocking)
        };
        Assert.True (state.RecordPinSnapshot (source));
        source ["Vendor.Package"] = PinState.Unpinned;
        Package package = new () { Id = "Vendor.Package", Name = "Package" };
        Assert.True (state.ApplyPinSnapshot ([package]));
        Assert.True (package.PinState.IsPinned);

        Assert.True (state.RecordPinSnapshot (new Dictionary<string, PinState> ()));
        Assert.True (state.ApplyPinSnapshot ([package]));
        Assert.Equal (PinState.Unpinned, package.PinState);
    }

    [Fact]
    public void SuccessfulPinMutationImmediatelyInvalidatesPinAuthorityAndFilter ()
    {
        AppState state = new (new MockBackend ())
        {
            Packages =
            [
                new () { Id = "Pinned", Name = "Pinned", PinState = new (PinStateKind.Blocking) },
                new () { Id = "Other", Name = "Other" }
            ],
            PinFilter = PinFilter.PinnedOnly
        };
        Assert.True (state.RecordPinSnapshot (new Dictionary<string, PinState>
        {
            ["Pinned"] = new (PinStateKind.Blocking)
        }));
        state.ApplyFilter ();
        Assert.Single (state.Filtered);
        OpResult result = new ()
        {
            Operation = new () { Kind = OperationKind.Unpin, PackageId = "Pinned" },
            Success = true,
            Message = "Unpinned"
        };

        Assert.True (App.MarkPinsStaleAfterSuccessfulMutation (state, result));
        state.ApplyFilter ();
        Assert.False (state.PinDataFresh);
        Assert.Equal (PinFilter.All, state.PinFilter);
        Assert.Equal (2, state.Filtered.Count);
        Assert.False (state.CyclePinFilter ());
    }

    [Fact]
    public async Task MockBackend_PinRefreshChoosesOppositeActionAndUnpinClearsState ()
    {
        MockBackend backend = new ();
        Package initial = Assert.Single (
            await backend.ListInstalledAsync (null, CancellationToken.None),
            package => package.Id == "Git.Git");
        Assert.False (initial.PinState.IsPinned);

        await backend.PinAsync (initial.Id, CancellationToken.None);
        Package afterPin = Assert.Single (
            await backend.SearchAsync ("Git.Git", null, CancellationToken.None),
            package => package.Id == "Git.Git");
        Assert.True (afterPin.PinState.IsPinned);

        await backend.UnpinAsync (afterPin.Id, CancellationToken.None);
        Package afterUnpin = Assert.Single (
            await backend.ListUpgradesAsync (null, CancellationToken.None),
            package => package.Id == "Git.Git");
        Assert.Equal (PinState.Unpinned, afterUnpin.PinState);
    }

    [Fact]
    public async Task MockBackend_ReturnedPackagesAreIsolatedCopies ()
    {
        MockBackend backend = new ();
        Package first = Assert.Single (
            await backend.ListInstalledAsync (null, CancellationToken.None),
            package => package.Id == "Git.Git");
        first.PinState = new (PinStateKind.Blocking);

        Package second = Assert.Single (
            await backend.ListInstalledAsync (null, CancellationToken.None),
            package => package.Id == "Git.Git");

        Assert.Equal ("Git", second.Name);
        Assert.Equal (PinState.Unpinned, second.PinState);
        Assert.NotSame (first, second);
    }

    [Fact]
    public async Task MockBackend_ReturnedPinDictionaryIsIsolatedSnapshot ()
    {
        MockBackend backend = new ();
        await backend.PinAsync ("Git.Git", CancellationToken.None);
        IReadOnlyDictionary<string, PinState> first = await backend.ListPinsAsync (CancellationToken.None);
        Dictionary<string, PinState> mutable = Assert.IsType<Dictionary<string, PinState>> (first);
        mutable.Clear ();
        mutable ["Injected.Package"] = new (PinStateKind.Blocking);

        IReadOnlyDictionary<string, PinState> second = await backend.ListPinsAsync (CancellationToken.None);

        Assert.True (second.ContainsKey ("git.git"));
        Assert.False (second.ContainsKey ("Injected.Package"));
    }

    [Fact]
    public async Task MockBackend_ConcurrentPinUnpinAndListDoesNotCorruptState ()
    {
        MockBackend backend = new ();
        Task [] workers = Enumerable.Range (0, 12)
                                    .Select (worker => Task.Run (async () =>
                                    {
                                        string id = $"Package.{worker}";

                                        for (int iteration = 0; iteration < 100; iteration++)
                                        {
                                            await backend.PinAsync (id, CancellationToken.None);
                                            _ = await backend.ListPinsAsync (CancellationToken.None);
                                            _ = await backend.ListInstalledAsync (null, CancellationToken.None);
                                            await backend.UnpinAsync (id, CancellationToken.None);
                                        }
                                    }))
                                    .ToArray ();

        await Task.WhenAll (workers);
        Assert.Empty (await backend.ListPinsAsync (CancellationToken.None));
    }

    [Fact]
    public async Task MockBackend_HonorsAlreadyCancelledRequests ()
    {
        using CancellationTokenSource cancellation = new ();
        cancellation.Cancel ();

        await Assert.ThrowsAnyAsync<OperationCanceledException> (
            () => new MockBackend ().SearchAsync ("git", null, cancellation.Token));
    }

    [Fact]
    public void LaunchUrl_DisposesReturnedProcessHandle ()
    {
        TrackingDisposable handle = new ();
        ProcessStartInfo? observed = null;

        App.LaunchUrl ("https://example.invalid/path", psi =>
                                                       {
                                                           observed = psi;

                                                           return handle;
                                                       });

        Assert.True (handle.Disposed);
        Assert.NotNull (observed);
        Assert.Equal ("https://example.invalid/path", observed.FileName);
        Assert.True (observed.UseShellExecute);
    }

    private static KeyValuePair<string, PinState> Pair (string id) =>
        new (id, new (PinStateKind.Blocking));

    private sealed class CountingPinDictionary (
        int reportedCount,
        IEnumerable<KeyValuePair<string, PinState>> entries) : IReadOnlyDictionary<string, PinState>
    {
        public int Count => reportedCount;
        public IEnumerable<string> Keys => throw new NotSupportedException ();
        public IEnumerable<PinState> Values => throw new NotSupportedException ();
        public PinState this [string key] => throw new KeyNotFoundException (key);
        internal int EnumeratorRequests { get; private set; }
        internal int MoveNextCount { get; private set; }

        public bool ContainsKey (string key) => false;

        public bool TryGetValue (string key, out PinState value)
        {
            value = default;

            return false;
        }

        public IEnumerator<KeyValuePair<string, PinState>> GetEnumerator ()
        {
            EnumeratorRequests++;

            return CountEntries ().GetEnumerator ();
        }

        IEnumerator IEnumerable.GetEnumerator () => GetEnumerator ();

        private IEnumerable<KeyValuePair<string, PinState>> CountEntries ()
        {
            foreach (KeyValuePair<string, PinState> entry in entries)
            {
                MoveNextCount++;

                yield return entry;
            }
        }
    }

    private sealed class TrackingDisposable : IDisposable
    {
        internal bool Disposed { get; private set; }

        public void Dispose () => Disposed = true;
    }
}
