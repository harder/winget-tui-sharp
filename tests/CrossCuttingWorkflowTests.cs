using System.Diagnostics;

namespace WingetTuiSharp.Tests;

public sealed class CrossCuttingWorkflowTests
{
    [Fact]
    public void StatusOwnership_OperationDominatesEveryAmbientWorkflowUntilRelease ()
    {
        StatusOwnership ownership = new ();
        string status = "Installing package · Esc to cancel";
        bool isError = false;
        (string Name, bool Error) [] ambientAttempts =
        [
            ("Loading Installed…", false),
            ("12 packages", false),
            ("List error", true),
            ("Press / to search for packages", false),
            ("Detail error", true),
            ("Background admission error", true),
            ("Opened https://example.invalid/", false),
            ("Exporting 12 rows…", false),
            ("Exported 12 rows", false)
        ];

        foreach ((string message, bool error) in ambientAttempts)
        {
            bool written = ownership.TryWrite (StatusOwner.Ambient, operationActive: true, () =>
                                                                                               {
                                                                                                   status = message;
                                                                                                   isError = error;
                                                                                               });
            Assert.False (written);
            Assert.Equal ("Installing package · Esc to cancel", status);
            Assert.False (isError);
        }

        Assert.True (ownership.TryWrite (StatusOwner.Operation, operationActive: true, () =>
                                                                                            {
                                                                                                status = "Cancelling…";
                                                                                                isError = true;
                                                                                            }));
        Assert.Equal ("Cancelling…", status);
        Assert.True (isError);

        Assert.True (ownership.TryWrite (StatusOwner.Ambient, operationActive: false, () =>
                                                                                              {
                                                                                                  status = "12 packages";
                                                                                                  isError = false;
                                                                                              }));
        Assert.Equal ("12 packages", status);
        Assert.False (isError);
    }

    [Theory]
    [InlineData (true, false, false)]
    [InlineData (false, true, false)]
    [InlineData (true, true, false)]
    [InlineData (false, false, true)]
    public void CanStartExport_RefusesOperationsAndDuplicateExports (
        bool operationActive,
        bool exportActive,
        bool expected) =>
        Assert.Equal (expected, App.CanStartExport (operationActive, exportActive));

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
