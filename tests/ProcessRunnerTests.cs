using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace WingetTuiSharp.Tests;

public sealed class ProcessRunnerTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine (Path.GetTempPath (), $"winget-tui-process-tests-{Guid.NewGuid ():N}");
    private readonly Dictionary<int, TrackedProcess> _childProcesses = [];

    public ProcessRunnerTests () => Directory.CreateDirectory (_tempDirectory);

    [Fact]
    public async Task RunWithCodeAsync_ReturnsStdoutThenStderrAndExitCode ()
    {
        FakeCommand command = CreateScript (
            "normal",
            unix: "printf 'standard-output'; printf 'standard-error' >&2; exit 7",
            windows: "[Console]::Out.Write('standard-output'); [Console]::Error.Write('standard-error'); exit 7");

        (int code, string output) = await CliBackend.RunWithCodeAsync (
            command.Arguments,
            command.Executable,
            TimeSpan.FromSeconds (10),
            TestContext.Current.CancellationToken);

        Assert.Equal (7, code);
        Assert.Equal ("standard-outputstandard-error", output);
    }

    [Fact]
    public async Task RunWithCodeAsync_PreservesArgumentBoundariesWithoutShellExpansion ()
    {
        const string payload = "spaces ; $(not-a-command) & 'quotes' \"double\"";
        FakeCommand command = CreateScript (
            "arguments",
            unix: "printf '%s' \"$1\"",
            windows: "[Console]::Out.Write($args[0])");
        IReadOnlyList<string> arguments = [.. command.Arguments, payload];

        (int code, string output) = await CliBackend.RunWithCodeAsync (
            arguments,
            command.Executable,
            TimeSpan.FromSeconds (10),
            TestContext.Current.CancellationToken);

        Assert.Equal (0, code);
        Assert.Equal (payload, output);
    }

    [Fact]
    public async Task WaitForPosixExitWithoutReaping_KeepsExitedLeaderIdentityUntilGroupCleanup ()
    {
        if (OperatingSystem.IsWindows ())
        {
            return;
        }

        FakeCommand command = CreateScript ("waitable-leader", unix: "exit 7", windows: string.Empty);
        bool observedWaitableLeader = false;

        (int code, _) = await ProcessRunner.RunWithPosixWaitObserverForTestAsync (
            command.Executable,
            command.Arguments,
            Encoding.UTF8,
            TimeSpan.FromSeconds (10),
            pid =>
            {
                // The runner already observed exit with WNOWAIT. A second WNOWAIT can succeed only
                // while cleanup still owns the same unreaped leader identity.
                ProcessRunner.WaitForPosixExitWithoutReapingForTest (pid);
                observedWaitableLeader = true;
            },
            TestContext.Current.CancellationToken);

        Assert.True (observedWaitableLeader);
        Assert.Equal (7, code);
    }

    [Fact]
    public async Task PosixSpawn_EstablishesDedicatedProcessGroupBeforeExec ()
    {
        if (OperatingSystem.IsWindows ())
        {
            return;
        }

        string pidFile = Path.Combine (_tempDirectory, "process-group.pid");
        string escapedPidFile = pidFile.Replace ("'", "'\\''", StringComparison.Ordinal);
        FakeCommand command = CreateScript (
            "process-group",
            unix: $"printf '%s' \"$$\" > '{escapedPidFile}'; exec sleep 60",
            windows: string.Empty);
        using CancellationTokenSource cancellation = new ();
        Task<(int Code, string Output)> run = ProcessRunner.RunAsync (
            command.Executable,
            command.Arguments,
            Encoding.UTF8,
            TimeSpan.FromSeconds (30),
            cancellation.Token);
        int childPid = await ReadPidAsync (pidFile, TestContext.Current.CancellationToken);
        Assert.True (TrackChild (childPid));

        Assert.Equal (childPid, ProcessRunner.GetPosixProcessGroupForTest (childPid));
        cancellation.Cancel ();
        await Assert.ThrowsAnyAsync<OperationCanceledException> (() => run);
        await AssertProcessStopsAsync (childPid);
    }

    [Fact]
    public async Task RunWithPosixLifecycleBarrier_CancellationDoesNotSignalAfterReap ()
    {
        if (OperatingSystem.IsWindows ())
        {
            return;
        }

        FakeCommand command = CreateScript ("reap-race", unix: "exit 0", windows: string.Empty);
        TaskCompletionSource workerOwnsLifecycle = new (TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim releaseWorker = new ();
        using CancellationTokenSource cancellation = new ();
        List<bool> terminationDecisions = [];
        object decisionsGate = new ();
        Task<(int Code, string Output)> run = ProcessRunner.RunWithPosixLifecycleBarrierForTestAsync (
            command.Executable,
            command.Arguments,
            Encoding.UTF8,
            TimeSpan.FromSeconds (30),
            _ =>
            {
                workerOwnsLifecycle.TrySetResult ();

                if (!releaseWorker.Wait (TimeSpan.FromSeconds (10)))
                {
                    throw new TimeoutException ("The test did not release POSIX lifecycle cleanup.");
                }
            },
            willSignal =>
            {
                lock (decisionsGate)
                {
                    terminationDecisions.Add (willSignal);
                }
            },
            cancellation.Token);

        try
        {
            await workerOwnsLifecycle.Task.WaitAsync (TimeSpan.FromSeconds (10), TestContext.Current.CancellationToken);
            Stopwatch cancellationDuration = Stopwatch.StartNew ();
            cancellation.Cancel ();
            cancellationDuration.Stop ();
            Assert.InRange (cancellationDuration.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds (1));
            Assert.False (run.IsCompleted);
            releaseWorker.Set ();
        }
        finally
        {
            releaseWorker.Set ();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException> (
            () => run.WaitAsync (TimeSpan.FromSeconds (10), TestContext.Current.CancellationToken));

        lock (decisionsGate)
        {
            Assert.Equal ([true, false], terminationDecisions);
        }
    }

    [Fact]
    public async Task ConcurrentPosixLaunch_DoesNotStartSecondTargetOrCoupleFirstPipeEof ()
    {
        if (OperatingSystem.IsWindows ())
        {
            return;
        }

        FakeCommand firstCommand = CreateScript ("launch-a", unix: "printf 'first-output'", windows: string.Empty);
        string secondMarker = Path.Combine (_tempDirectory, "launch-b-started");
        string escapedMarker = secondMarker.Replace ("'", "'\\''", StringComparison.Ordinal);
        FakeCommand secondCommand = CreateScript (
            "launch-b",
            unix: $"printf 'started' > '{escapedMarker}'; sleep 60",
            windows: string.Empty);
        TaskCompletionSource firstPipesCreated = new (TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim releaseFirstLaunch = new ();
        using CancellationTokenSource secondCancellation = new ();
        Task<(int Code, string Output)> firstRun = Task.Run (
            () => ProcessRunner.RunWithPosixLaunchBarrierForTestAsync (
                firstCommand.Executable,
                firstCommand.Arguments,
                Encoding.UTF8,
                TimeSpan.FromSeconds (20),
                () =>
                {
                    firstPipesCreated.TrySetResult ();

                    if (!releaseFirstLaunch.Wait (TimeSpan.FromSeconds (10)))
                    {
                        throw new TimeoutException ("The test did not release the first POSIX launch.");
                    }
                },
                TestContext.Current.CancellationToken));

        await firstPipesCreated.Task.WaitAsync (TimeSpan.FromSeconds (10), TestContext.Current.CancellationToken);
        Task<(int Code, string Output)> secondRun = Task.Run (
            () => CliBackend.RunWithCodeAsync (
                secondCommand.Arguments,
                secondCommand.Executable,
                TimeSpan.FromSeconds (20),
                secondCancellation.Token));

        try
        {
            try
            {
                await Task.Delay (200, TestContext.Current.CancellationToken);
                Assert.False (File.Exists (secondMarker));
            }
            finally
            {
                releaseFirstLaunch.Set ();
            }

            await WaitForFileAsync (secondMarker, TestContext.Current.CancellationToken);
            (int firstCode, string firstOutput) = await firstRun.WaitAsync (
                TimeSpan.FromSeconds (3),
                TestContext.Current.CancellationToken);
            Assert.Equal (0, firstCode);
            Assert.Equal ("first-output", firstOutput);
        }
        finally
        {
            releaseFirstLaunch.Set ();
            secondCancellation.Cancel ();

            await Assert.ThrowsAnyAsync<OperationCanceledException> (
                () => secondRun.WaitAsync (TimeSpan.FromSeconds (10), TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task LinuxPipeCreation_DoesNotLeakIntoConcurrentProcessStart ()
    {
        if (!OperatingSystem.IsLinux ())
        {
            return;
        }

        FakeCommand command = CreateScript ("pipe2", unix: "printf 'complete'", windows: string.Empty);
        TaskCompletionSource pipesCreated = new (TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim releaseLaunch = new ();
        Task<(int Code, string Output)> run = Task.Run (
            () => ProcessRunner.RunWithPosixLaunchBarrierForTestAsync (
                command.Executable,
                command.Arguments,
                Encoding.UTF8,
                TimeSpan.FromSeconds (10),
                () =>
                {
                    pipesCreated.TrySetResult ();
                    releaseLaunch.Wait (TimeSpan.FromSeconds (10));
                },
                TestContext.Current.CancellationToken));

        await pipesCreated.Task.WaitAsync (TimeSpan.FromSeconds (10), TestContext.Current.CancellationToken);
        using Process external = Process.Start (new ProcessStartInfo ("/bin/sh")
        {
            UseShellExecute = false,
            ArgumentList = { "-c", "sleep 3" }
        })!;

        try
        {
            Stopwatch completion = Stopwatch.StartNew ();
            releaseLaunch.Set ();
            (int code, string output) = await run.WaitAsync (
                TimeSpan.FromSeconds (2),
                TestContext.Current.CancellationToken);
            completion.Stop ();

            Assert.Equal (0, code);
            Assert.Equal ("complete", output);
            Assert.InRange (completion.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds (2));
        }
        finally
        {
            releaseLaunch.Set ();

            if (!external.HasExited)
            {
                external.Kill (entireProcessTree: true);
            }

            await external.WaitForExitAsync (TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task PosixRunner_CoexistsWithConcurrentManagedProcessStarts ()
    {
        if (OperatingSystem.IsWindows ())
        {
            return;
        }

        FakeCommand command = CreateScript ("managed-reaper", unix: "sleep 0.02; printf 'runner'", windows: string.Empty);
        Task<(int Code, string Output)> [] runnerTasks = Enumerable.Range (0, 20)
            .Select (_ => CliBackend.RunWithCodeAsync (
                         command.Arguments,
                         command.Executable,
                         TimeSpan.FromSeconds (10),
                         TestContext.Current.CancellationToken))
            .ToArray ();
        Task [] managedTasks = Enumerable.Range (0, 40).Select (_ => RunManagedProcessAsync ()).ToArray ();

        await Task.WhenAll (managedTasks);
        (int Code, string Output) [] results = await Task.WhenAll (runnerTasks);

        Assert.All (results, result =>
        {
            Assert.Equal (0, result.Code);
            Assert.Equal ("runner", result.Output);
        });

        async Task RunManagedProcessAsync ()
        {
            using Process process = Process.Start (new ProcessStartInfo ("/bin/sh")
            {
                UseShellExecute = false,
                ArgumentList = { "-c", "exit 0" }
            })!;
            await process.WaitForExitAsync (TestContext.Current.CancellationToken);
            Assert.Equal (0, process.ExitCode);
        }
    }

    [Fact]
    public async Task FinishDrain_DisposesActualReadersAndHonorsSingleCleanupBound ()
    {
        using MemoryStream stdoutStream = new ();
        using MemoryStream stderrStream = new ();
        using StreamReader stdout = new (stdoutStream);
        using StreamReader stderr = new (stderrStream);
        TaskCompletionSource neverCompletes = new (TaskCreationOptions.RunContinuationsAsynchronously);
        Stopwatch stopwatch = Stopwatch.StartNew ();

        bool completed = await ProcessRunner.FinishDrainForTestAsync (
            stdout,
            stderr,
            neverCompletes.Task,
            TimeSpan.FromMilliseconds (100));

        stopwatch.Stop ();
        Assert.False (completed);
        Assert.False (stdoutStream.CanRead);
        Assert.False (stderrStream.CanRead);
        Assert.InRange (stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds (2));
    }

    [Theory]
    [InlineData ("", "\"\"")]
    [InlineData ("plain", "plain")]
    [InlineData ("two words", "\"two words\"")]
    [InlineData ("a\"b", "\"a\\\"b\"")]
    [InlineData ("C:\\Program Files\\Thing\\", "\"C:\\Program Files\\Thing\\\\\"")]
    public void QuoteWindowsArgument_FollowsCreateProcessBackslashQuoteRules (string argument, string expected)
    {
        Assert.Equal (expected, ProcessRunner.QuoteWindowsArgument (argument));
    }

    [Fact]
    public void BuildWindowsCommandLine_QuotesExecutableAndKeepsArgumentBoundaries ()
    {
        string commandLine = ProcessRunner.BuildWindowsCommandLine (
            "C:\\Program Files\\winget.exe",
            ["search", "two words", "a\"b", string.Empty]);

        Assert.Equal ("\"C:\\Program Files\\winget.exe\" search \"two words\" \"a\\\"b\" \"\"", commandLine);
    }

    [Fact]
    public async Task RunWithCodeAsync_CallerCancellationTerminatesProcessTree ()
    {
        string pidFile = Path.Combine (_tempDirectory, "cancel-child.pid");
        FakeCommand command = CreateLongRunningScript ("cancel", pidFile);
        using CancellationTokenSource cancellation = new (TimeSpan.FromSeconds (15));
        Task<(int Code, string Output)> run = CliBackend.RunWithCodeAsync (
            command.Arguments,
            command.Executable,
            TimeSpan.FromMinutes (1),
            cancellation.Token);

        int childPid = await ReadPidAsync (pidFile, TestContext.Current.CancellationToken);
        TrackChild (childPid);
        cancellation.Cancel ();

        await Assert.ThrowsAnyAsync<OperationCanceledException> (() => run);
        await AssertProcessStopsAsync (childPid);
    }

    [Fact]
    public async Task RunWithCodeAsync_TimeoutTerminatesProcessTree ()
    {
        string pidFile = Path.Combine (_tempDirectory, "timeout-child.pid");
        FakeCommand command = CreateLongRunningScript ("timeout", pidFile);
        Task<(int Code, string Output)> run = CliBackend.RunWithCodeAsync (
            command.Arguments,
            command.Executable,
            TimeSpan.FromSeconds (2),
            TestContext.Current.CancellationToken);

        int childPid = await ReadPidAsync (pidFile, TestContext.Current.CancellationToken);
        TrackChild (childPid);

        TimeoutException exception = await Assert.ThrowsAsync<TimeoutException> (() => run);
        Assert.Contains ("deadline", exception.Message, StringComparison.OrdinalIgnoreCase);
        await AssertProcessStopsAsync (childPid);
    }

    [Fact]
    public async Task RunWithCodeAsync_TimeoutKillsPipeHoldingDescendantAfterParentExited ()
    {
        string pidFile = Path.Combine (_tempDirectory, "orphan-pipe-child.pid");
        FakeCommand command = CreateNestedOrphanWithLongRunningRoot ("orphan-pipe", pidFile);
        Task<(int Code, string Output)> run = CliBackend.RunWithCodeAsync (
            command.Arguments,
            command.Executable,
            TimeSpan.FromSeconds (3),
            TestContext.Current.CancellationToken);

        int childPid = await ReadPidAsync (pidFile, TestContext.Current.CancellationToken);
        TrackChild (childPid);
        await WaitForFileAsync (pidFile + ".parent-exited", TestContext.Current.CancellationToken);

        TimeoutException exception = await Assert.ThrowsAsync<TimeoutException> (() => run);
        Assert.Contains ("deadline", exception.Message, StringComparison.OrdinalIgnoreCase);
        await AssertProcessStopsAsync (childPid);
    }

    [Fact]
    public async Task RunWithCodeAsync_CancellationKillsPipeHoldingDescendantAfterParentExited ()
    {
        string pidFile = Path.Combine (_tempDirectory, "cancel-orphan-pipe-child.pid");
        FakeCommand command = CreateNestedOrphanWithLongRunningRoot ("cancel-orphan-pipe", pidFile);
        using CancellationTokenSource cancellation = new (TimeSpan.FromSeconds (15));
        Task<(int Code, string Output)> run = CliBackend.RunWithCodeAsync (
            command.Arguments,
            command.Executable,
            TimeSpan.FromMinutes (1),
            cancellation.Token);

        int childPid = await ReadPidAsync (pidFile, TestContext.Current.CancellationToken);
        TrackChild (childPid);
        await WaitForFileAsync (pidFile + ".parent-exited", TestContext.Current.CancellationToken);
        cancellation.Cancel ();

        await Assert.ThrowsAnyAsync<OperationCanceledException> (() => run);
        await AssertProcessStopsAsync (childPid);
    }

    [Fact]
    public async Task RunWithCodeAsync_SuccessKillsPipeHoldingDescendantAfterParentExitedImmediately ()
    {
        string pidFile = Path.Combine (_tempDirectory, "success-orphan-pipe-child.pid");
        string releaseFile = pidFile + ".release";
        FakeCommand command = CreateOrphaningScript ("success-orphan-pipe", pidFile, detachStandardHandles: false);
        Task<(int Code, string Output)> run = CliBackend.RunWithCodeAsync (
            command.Arguments,
            command.Executable,
            TimeSpan.FromSeconds (10),
            TestContext.Current.CancellationToken);

        int childPid = await ReadPidAsync (pidFile, TestContext.Current.CancellationToken);
        int code;

        try
        {
            Assert.True (TrackChild (childPid), "The descendant exited before its stable process identity was captured.");
            File.WriteAllText (releaseFile, "release");
            (code, _) = await run;
        }
        finally
        {
            File.WriteAllText (releaseFile, "release");
        }

        Assert.Equal (0, code);
        await AssertProcessStopsAsync (childPid);
    }

    [Fact]
    public async Task RunWithCodeAsync_SuccessKillsDetachedDescendantThatClosedPipes ()
    {
        string pidFile = Path.Combine (_tempDirectory, "orphan-detached-child.pid");
        string releaseFile = pidFile + ".release";
        FakeCommand command = CreateOrphaningScript ("orphan-detached", pidFile, detachStandardHandles: true);
        Task<(int Code, string Output)> run = CliBackend.RunWithCodeAsync (
            command.Arguments,
            command.Executable,
            TimeSpan.FromSeconds (10),
            TestContext.Current.CancellationToken);

        int childPid = await ReadPidAsync (pidFile, TestContext.Current.CancellationToken);
        int code;

        try
        {
            Assert.True (TrackChild (childPid), "The descendant exited before its stable process identity was captured.");
            File.WriteAllText (releaseFile, "release");
            (code, _) = await run;
        }
        finally
        {
            File.WriteAllText (releaseFile, "release");
        }

        Assert.Equal (0, code);
        await AssertProcessStopsAsync (childPid);
    }

    [Fact]
    public async Task RunWithCodeAsync_DrainsFloodedPipesButBoundsRetainedOutput ()
    {
        const int floodCharacters = ProcessRunner.MaxCapturedStdoutCharacters + 4096;
        FakeCommand command = CreateScript (
            "flood",
            unix: $"head -c {floodCharacters} /dev/zero | tr '\\0' A; head -c {floodCharacters} /dev/zero | tr '\\0' B >&2",
            windows: $"[Console]::Out.Write('A' * {floodCharacters}); [Console]::Error.Write('B' * {floodCharacters})");

        (int code, string output) = await CliBackend.RunWithCodeAsync (
            command.Arguments,
            command.Executable,
            TimeSpan.FromSeconds (30),
            TestContext.Current.CancellationToken);

        Assert.Equal (0, code);
        Assert.InRange (output.Length, 1, ProcessRunner.MaxCombinedCapturedCharacters);
        Assert.Equal (2, CountOccurrences (output, ProcessRunner.TruncationMarker));
        Assert.StartsWith (new string ('A', 32), output, StringComparison.Ordinal);
        Assert.Contains (new string ('B', 32), output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunDetailedWithCodeAsync_NormalOutputIsComplete ()
    {
        FakeCommand command = CreateScript (
            "detailed-normal",
            unix: "printf 'complete'",
            windows: "[Console]::Out.Write('complete')");

        ProcessRunner.RunResult result = await CliBackend.RunDetailedWithCodeAsync (
                                             command.Arguments,
                                             command.Executable,
                                             TimeSpan.FromSeconds (10),
                                             TestContext.Current.CancellationToken);

        Assert.True (result.OutputComplete);
        Assert.False (result.StdoutTruncated);
        Assert.False (result.StderrTruncated);
        Assert.False (result.CleanupIncomplete);
        Assert.Equal ("complete", result.Output);
    }

    [Fact]
    public async Task RunDetailedWithCodeAsync_FloodReportsIncompleteCapture ()
    {
        const int floodCharacters = ProcessRunner.MaxCapturedStdoutCharacters + 4096;
        FakeCommand command = CreateScript (
            "detailed-flood",
            unix: $"head -c {floodCharacters} /dev/zero | tr '\\0' A",
            windows: $"[Console]::Out.Write('A' * {floodCharacters})");

        ProcessRunner.RunResult result = await CliBackend.RunDetailedWithCodeAsync (
                                             command.Arguments,
                                             command.Executable,
                                             TimeSpan.FromSeconds (30),
                                             TestContext.Current.CancellationToken);

        Assert.False (result.OutputComplete);
        Assert.True (result.StdoutTruncated);
        Assert.False (result.StderrTruncated);
        Assert.Contains (ProcessRunner.TruncationMarker, result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureCompleteForParsing_RejectsEveryIncompleteReason ()
    {
        ProcessRunner.RunResult [] results =
        [
            new (0, "partial", StdoutTruncated: true, StderrTruncated: false, CleanupIncomplete: false),
            new (0, "partial", StdoutTruncated: false, StderrTruncated: true, CleanupIncomplete: false),
            new (0, "partial", StdoutTruncated: false, StderrTruncated: false, CleanupIncomplete: true)
        ];

        foreach (ProcessRunner.RunResult result in results)
        {
            BoundedOutputException exception = Assert.Throws<BoundedOutputException> (
                () => CliBackend.EnsureCompleteForParsing (result, ["search"]));
            Assert.Contains ("incomplete output", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task RunParsedForTestAsync_DoesNotInvokeParserForTruncatedOutput ()
    {
        const int floodCharacters = ProcessRunner.MaxCapturedStdoutCharacters + 4096;
        FakeCommand command = CreateScript (
            "parsed-flood",
            unix: $"head -c {floodCharacters} /dev/zero | tr '\\0' A",
            windows: $"[Console]::Out.Write('A' * {floodCharacters})");
        bool parserCalled = false;

        await Assert.ThrowsAsync<BoundedOutputException> (
            () => CliBackend.RunParsedForTestAsync (
                command.Arguments,
                command.Executable,
                TimeSpan.FromSeconds (30),
                _ =>
                {
                    parserCalled = true;

                    return 0;
                },
                TestContext.Current.CancellationToken));

        Assert.False (parserCalled);
    }

    public void Dispose ()
    {
        foreach (TrackedProcess process in _childProcesses.Values)
        {
            KillIfAlive (process);
        }

        try
        {
            Directory.Delete (_tempDirectory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
            // Antivirus/process-handle races on Windows should not obscure the process assertion.
        }
    }

    private FakeCommand CreateLongRunningScript (string name, string pidFile)
    {
        string escapedUnixPidFile = pidFile.Replace ("'", "'\\''", StringComparison.Ordinal);
        string escapedPowerShellPidFile = pidFile.Replace ("'", "''", StringComparison.Ordinal);

        return CreateScript (
            name,
            unix: $"sleep 60 & child=$!; printf '%s' \"$child\" > '{escapedUnixPidFile}'; wait \"$child\"",
            windows: $"$childProcess = Start-Process pwsh -ArgumentList '-NoProfile','-Command','Start-Sleep 60' -PassThru; "
                     + $"[IO.File]::WriteAllText('{escapedPowerShellPidFile}', $childProcess.Id.ToString([CultureInfo]::InvariantCulture)); "
                     + "Wait-Process -Id $childProcess.Id");
    }

    private FakeCommand CreateOrphaningScript (string name, string pidFile, bool detachStandardHandles)
    {
        string escapedUnixPidFile = pidFile.Replace ("'", "'\\''", StringComparison.Ordinal);
        string escapedPowerShellPidFile = pidFile.Replace ("'", "''", StringComparison.Ordinal);
        string escapedUnixReleaseFile = (pidFile + ".release").Replace ("'", "'\\''", StringComparison.Ordinal);
        string escapedPowerShellReleaseFile = (pidFile + ".release").Replace ("'", "''", StringComparison.Ordinal);
        string unixRedirection = detachStandardHandles ? " >/dev/null 2>&1" : string.Empty;
        string windowsRedirection = detachStandardHandles
                                        ? $" -RedirectStandardOutput '{escapedPowerShellPidFile}.out' -RedirectStandardError '{escapedPowerShellPidFile}.err'"
                                        : string.Empty;

        return CreateScript (
            name,
            unix: $"sleep 60{unixRedirection} & child=$!; printf '%s' \"$child\" > '{escapedUnixPidFile}'; "
                  + $"while [ ! -f '{escapedUnixReleaseFile}' ]; do sleep 0.01; done; exit 0",
            windows: $"$childProcess = Start-Process pwsh -ArgumentList '-NoProfile','-Command','Start-Sleep 60' -PassThru{windowsRedirection}; "
                     + $"[IO.File]::WriteAllText('{escapedPowerShellPidFile}', $childProcess.Id.ToString([CultureInfo]::InvariantCulture)); "
                     + $"while (!(Test-Path -LiteralPath '{escapedPowerShellReleaseFile}')) {{ Start-Sleep -Milliseconds 10 }}; exit 0");
    }

    private FakeCommand CreateNestedOrphanWithLongRunningRoot (string name, string pidFile)
    {
        string escapedUnixPidFile = pidFile.Replace ("'", "'\\''", StringComparison.Ordinal);
        string escapedPowerShellPidFile = pidFile.Replace ("'", "''", StringComparison.Ordinal);
        string escapedUnixExitedFile = (pidFile + ".parent-exited").Replace ("'", "'\\''", StringComparison.Ordinal);
        string escapedPowerShellExitedFile = (pidFile + ".parent-exited").Replace ("'", "''", StringComparison.Ordinal);

        if (OperatingSystem.IsWindows ())
        {
            string intermediate = Path.Combine (_tempDirectory, name + "-intermediate.ps1");
            File.WriteAllText (
                intermediate,
                "$childProcess = Start-Process pwsh -ArgumentList '-NoProfile','-Command','Start-Sleep 60' -PassThru; "
                + $"[IO.File]::WriteAllText('{escapedPowerShellPidFile}', $childProcess.Id.ToString([CultureInfo]::InvariantCulture)); exit 0",
                new UTF8Encoding (encoderShouldEmitUTF8Identifier: false));
            string escapedIntermediate = intermediate.Replace ("'", "''", StringComparison.Ordinal);

            return CreateScript (
                name,
                unix: string.Empty,
                windows: $"$intermediate = Start-Process pwsh -ArgumentList '-NoLogo','-NoProfile','-NonInteractive','-File','{escapedIntermediate}' -PassThru; "
                         + $"$intermediate.WaitForExit(); [IO.File]::WriteAllText('{escapedPowerShellExitedFile}', 'exited'); Start-Sleep 60");
        }

        string intermediateUnix = Path.Combine (_tempDirectory, name + "-intermediate.sh");
        File.WriteAllText (
            intermediateUnix,
            $"#!/bin/sh\nsleep 60 & child=$!\nprintf '%s' \"$child\" > '{escapedUnixPidFile}'\nexit 0\n",
            new UTF8Encoding (encoderShouldEmitUTF8Identifier: false));

        return CreateScript (
            name,
            unix: $"/bin/sh '{intermediateUnix.Replace ("'", "'\\''", StringComparison.Ordinal)}'; printf 'exited' > '{escapedUnixExitedFile}'; sleep 60",
            windows: string.Empty);
    }

    private FakeCommand CreateScript (string name, string unix, string windows)
    {
        if (OperatingSystem.IsWindows ())
        {
            string path = Path.Combine (_tempDirectory, name + ".ps1");
            File.WriteAllText (path, windows, new UTF8Encoding (encoderShouldEmitUTF8Identifier: false));

            return new ("pwsh", ["-NoLogo", "-NoProfile", "-NonInteractive", "-File", path]);
        }

        string unixPath = Path.Combine (_tempDirectory, name + ".sh");
        File.WriteAllText (unixPath, "#!/bin/sh\nset -eu\n" + unix + "\n", new UTF8Encoding (encoderShouldEmitUTF8Identifier: false));

        return new ("/bin/sh", [unixPath]);
    }

    private static async Task<int> ReadPidAsync (string path, CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds (10);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested ();

            if (File.Exists (path))
            {
                string? text = null;

                try
                {
                    text = await File.ReadAllTextAsync (path, cancellationToken);
                }
                catch (IOException)
                {
                    // The writer may have created the file but not released its handle yet.
                    // Retry until the complete PID is readable or the deadline expires.
                }

                if (text is not null
                    && int.TryParse (text, NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
                {
                    return pid;
                }
            }

            await Task.Delay (25, cancellationToken);
        }

        throw new TimeoutException ($"The fake process did not write its child pid to '{path}'.");
    }

    private static async Task WaitForFileAsync (string path, CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds (10);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested ();

            if (File.Exists (path))
            {
                return;
            }

            await Task.Delay (10, cancellationToken);
        }

        throw new TimeoutException ($"The fake process did not publish '{path}'.");
    }

    private bool TrackChild (int pid)
    {
        try
        {
            using Process process = Process.GetProcessById (pid);
            _childProcesses [pid] = new (pid, process.StartTime.ToUniversalTime ());

            return true;
        }
        catch (ArgumentException)
        {
            // It already stopped; never retain a bare PID that could later be reused.

            return false;
        }
        catch (InvalidOperationException)
        {
            // It exited while its stable start identity was being captured.

            return false;
        }
    }

    private async Task AssertProcessStopsAsync (int pid)
    {
        if (!_childProcesses.TryGetValue (pid, out TrackedProcess? identity))
        {
            return;
        }

        DateTime deadline = DateTime.UtcNow.AddSeconds (10);

        while (DateTime.UtcNow < deadline)
        {
            if (!IsSameProcessAlive (identity))
            {
                _childProcesses.Remove (pid);

                return;
            }

            await Task.Delay (25, TestContext.Current.CancellationToken);
        }

        bool stillAlive = IsSameProcessAlive (identity);

        if (!stillAlive)
        {
            _childProcesses.Remove (pid);
        }

        Assert.False (stillAlive, $"Descendant process {pid} survived cancellation/timeout.");
    }

    private static bool IsSameProcessAlive (TrackedProcess identity)
    {
        if (OperatingSystem.IsLinux ())
        {
            try
            {
                string stat = File.ReadAllText ($"/proc/{identity.ProcessId}/stat");
                int commandEnd = stat.LastIndexOf (')');

                // A container test runner may be PID 1 and leave killed orphan descendants as
                // zombies. They cannot execute or hold pipes/resources, so count them as stopped.
                if (commandEnd >= 0 && commandEnd + 2 < stat.Length && stat [commandEnd + 2] == 'Z')
                {
                    return false;
                }
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
            catch (IOException)
            {
                // procfs entries can disappear after the open but before the read completes.
                return false;
            }
        }

        try
        {
            using Process process = Process.GetProcessById (identity.ProcessId);

            return !process.HasExited && process.StartTime.ToUniversalTime () == identity.StartTimeUtc;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void KillIfAlive (TrackedProcess identity)
    {
        try
        {
            using Process process = Process.GetProcessById (identity.ProcessId);

            if (!process.HasExited && process.StartTime.ToUniversalTime () == identity.StartTimeUtc)
            {
                process.Kill (entireProcessTree: true);
                process.WaitForExit (5000);
            }
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static int CountOccurrences (string value, string fragment)
    {
        int count = 0;
        int index = 0;

        while ((index = value.IndexOf (fragment, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += fragment.Length;
        }

        return count;
    }

    private sealed record FakeCommand (string Executable, IReadOnlyList<string> Arguments);
    private sealed record TrackedProcess (int ProcessId, DateTime StartTimeUtc);
}
