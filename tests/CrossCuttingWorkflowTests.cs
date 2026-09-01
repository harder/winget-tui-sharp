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
    public void ApplyPinStates_IsCaseInsensitiveAndClearsAbsentPins ()
    {
        Package pinned = new () { Id = "Vendor.Package", Name = "Package", PinState = PinState.Unpinned };
        Package removed = new () { Id = "Vendor.Removed", Name = "Removed", PinState = new (PinStateKind.Blocking) };
        Dictionary<string, PinState> snapshot = new ()
        {
            ["vendor.package"] = new (PinStateKind.Gating, "2.0")
        };

        App.ApplyPinStates ([pinned, removed], snapshot);

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
        state.RecordPinSnapshot (new Dictionary<string, PinState>
        {
            ["Vendor.Pinned"] = new (PinStateKind.Blocking)
        });
        Assert.True (state.PinDataFresh);
        Assert.Equal (1, state.PinSnapshotCount);

        state.MarkPinsStale ();
        Assert.False (state.PinDataFresh);
        Assert.True (state.HasPinSnapshot);
        Assert.Equal (1, state.PinSnapshotCount);

        state.RecordPinSnapshot (new Dictionary<string, PinState> ());
        Assert.True (state.PinDataFresh);
        Assert.True (state.HasPinSnapshot);
        Assert.Equal (0, state.PinSnapshotCount);
    }

    [Fact]
    public void PinSnapshot_IsBoundedAndWarningDefersUnderOperation ()
    {
        AppState state = new (new MockBackend ());
        Dictionary<string, PinState> pins = Enumerable.Range (0, BoundedPinSnapshot.MaxEntries + 100)
                                                      .ToDictionary (
                                                          value => $"Package.{value:D6}",
                                                          _ => new PinState (PinStateKind.Blocking));
        state.RecordPinSnapshot (pins);
        Assert.Equal (BoundedPinSnapshot.MaxEntries, state.PinSnapshotCount);

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

    private sealed class TrackingDisposable : IDisposable
    {
        internal bool Disposed { get; private set; }

        public void Dispose () => Disposed = true;
    }
}
