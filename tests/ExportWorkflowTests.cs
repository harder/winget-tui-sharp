namespace WingetTuiSharp.Tests;

public sealed class ExportWorkflowTests
{
    [Fact]
    public void TryBegin_AllowsOnlyOneActiveExport ()
    {
        using ExportWorkflowState workflow = new ();
        DisposableProbe firstLoading = new ();
        DisposableProbe rejectedLoading = new ();

        Assert.True (workflow.TryBegin (
            TestContext.Current.CancellationToken,
            "first",
            () => firstLoading,
            out ExportOperation first));
        Assert.False (workflow.TryBegin (
            TestContext.Current.CancellationToken,
            "second",
            () => rejectedLoading,
            out _));

        Assert.True (workflow.IsActive);
        Assert.Equal (0, firstLoading.DisposeCount);
        Assert.Equal (1, rejectedLoading.DisposeCount);

        ExportCompletion completion = workflow.Complete (first, "first");
        Assert.True (completion.WasCurrent);
        Assert.True (completion.OwnedStatus);
        Assert.False (workflow.IsActive);
        Assert.Equal (1, firstLoading.DisposeCount);
    }

    [Fact]
    public async Task AdmissionRejection_ReleasesOwnershipAndReturnsReport ()
    {
        using BackgroundTaskTracker tracker = new (maxOutstandingTasks: 1);
        TaskCompletionSource releaseBlocker = new (TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True (tracker.TryRun (_ => releaseBlocker.Task));
        using ExportWorkflowState workflow = new ();
        DisposableProbe loading = new ();
        Assert.True (workflow.TryBegin (
            tracker.LifetimeToken,
            "exporting",
            () => loading,
            out ExportOperation operation));

        Assert.False (tracker.TryRun (_ => Task.CompletedTask));
        Assert.True (workflow.RejectAdmission (operation, out string message));

        Assert.Equal (ExportWorkflowState.AdmissionRejectedMessage, message);
        Assert.Equal (1, loading.DisposeCount);
        Assert.False (workflow.IsActive);
        releaseBlocker.SetResult ();
        Assert.True (await tracker.DrainAsync (TimeSpan.FromSeconds (2)));
    }

    [Fact]
    public async Task LifetimeCancellation_SettlesExportAndPreservesExistingFile ()
    {
        using TempDirectory temp = new ();
        string destination = Path.Combine (temp.Path, "export.csv");
        await File.WriteAllTextAsync (destination, "original", TestContext.Current.CancellationToken);
        using BackgroundTaskTracker tracker = new ();
        using ExportWorkflowState workflow = new ();
        DisposableProbe loading = new ();
        Assert.True (workflow.TryBegin (
            tracker.LifetimeToken,
            "exporting",
            () => loading,
            out ExportOperation operation));
        TaskCompletionSource reachedCommit = new (TaskCreationOptions.RunContinuationsAsynchronously);
        CsvSnapshot snapshot = CsvExporter.CreateSnapshot (
        [
            new Package { Id = "id", Name = "name", Version = "1.0", Source = "winget" }
        ]);

        Assert.True (tracker.TryRun (async _ =>
                                     {
                                         try
                                         {
                                             await CsvExporter.WriteAtomicAsync (
                                                 destination,
                                                 snapshot,
                                                 operation.Token,
                                                 async cancellationToken =>
                                                 {
                                                     reachedCommit.TrySetResult ();
                                                     await Task.Delay (Timeout.InfiniteTimeSpan, cancellationToken);
                                                 });
                                         }
                                         finally
                                         {
                                             workflow.Release (operation);
                                         }
                                     }));

        await reachedCommit.Task.WaitAsync (TestContext.Current.CancellationToken);
        tracker.BeginStop ();
        Assert.True (await tracker.DrainAsync (TimeSpan.FromSeconds (2)));

        Assert.Equal ("original", await File.ReadAllTextAsync (destination, TestContext.Current.CancellationToken));
        Assert.Empty (Directory.GetFiles (temp.Path, ".export.csv.*.tmp"));
        Assert.False (workflow.IsActive);
        Assert.Equal (1, loading.DisposeCount);
    }

    [Fact]
    public void StaleCompletion_CannotClearNewOperationOrOwnNewerStatus ()
    {
        using ExportWorkflowState workflow = new ();
        Assert.True (workflow.TryBegin (
            TestContext.Current.CancellationToken,
            "old activity",
            () => new DisposableProbe (),
            out ExportOperation old));
        workflow.Release (old);
        Assert.True (workflow.TryBegin (
            TestContext.Current.CancellationToken,
            "new activity",
            () => new DisposableProbe (),
            out ExportOperation current));

        ExportCompletion stale = workflow.Complete (old, "new activity");

        Assert.False (stale.WasCurrent);
        Assert.False (stale.OwnedStatus);
        Assert.True (workflow.IsCurrent (current));
        ExportCompletion currentCompletion = workflow.Complete (current, "newer unrelated status");
        Assert.True (currentCompletion.WasCurrent);
        Assert.False (currentCompletion.OwnedStatus);
    }

    [Fact]
    public void OperationDispose_DisposesCancellationSourceWhenLoadingLeaseThrows ()
    {
        CancellationTokenSource cancellation = new ();
        ExportOperation operation = new (cancellation, new ThrowingDisposable (), "exporting");

        Assert.Throws<InvalidOperationException> (operation.Dispose);
        Assert.Throws<ObjectDisposedException> (() => _ = cancellation.Token);

        // Both fields were exchanged before callbacks ran, so retry cannot double-dispose.
        operation.Dispose ();
    }

    [Fact]
    public void CancellationCallbackFailureCannotInterruptCancelOrDispose ()
    {
        ExportWorkflowState workflow = new ();
        DisposableProbe loading = new ();
        Assert.True (workflow.TryBegin (
            CancellationToken.None,
            "exporting",
            () => loading,
            out ExportOperation operation));
        using CancellationTokenRegistration registration = operation.Token.Register (
            () => throw new InvalidOperationException ("callback failed"));

        workflow.CancelActive ();

        Assert.True (operation.Token.IsCancellationRequested);
        Assert.True (workflow.IsActive);
        Assert.Equal (0, loading.DisposeCount);

        workflow.Dispose ();

        Assert.False (workflow.IsActive);
        Assert.Equal (1, loading.DisposeCount);
        workflow.Dispose ();
        Assert.Equal (1, loading.DisposeCount);
    }

    private sealed class DisposableProbe : IDisposable
    {
        internal int DisposeCount { get; private set; }

        public void Dispose () => DisposeCount++;
    }

    private sealed class ThrowingDisposable : IDisposable
    {
        public void Dispose () => throw new InvalidOperationException ("lease cleanup failed");
    }

    private sealed class TempDirectory : IDisposable
    {
        internal TempDirectory ()
        {
            Path = System.IO.Path.Combine (System.IO.Path.GetTempPath (), $"winget-tui-workflow-{Guid.NewGuid ():N}");
            Directory.CreateDirectory (Path);
        }

        internal string Path { get; }

        public void Dispose () => Directory.Delete (Path, recursive: true);
    }
}
