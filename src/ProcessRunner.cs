using System.Buffers;
using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WingetTuiSharp;

/// <summary>Runs a child with finite lifetime, bounded output, and kernel process containment.</summary>
internal static class ProcessRunner
{
    // Inventory output can be large on heavily provisioned hosts. Keep generous but finite
    // stdout headroom while retaining a smaller diagnostic-only stderr budget.
    internal const int MaxCapturedStdoutCharacters = 4 * 1024 * 1024;
    internal const int MaxCapturedStderrCharacters = 1024 * 1024;
    internal const string TruncationMarker = "\n...[output truncated]...\n";
    internal const string DrainIncompleteMarker = "\n...[output drain incomplete]...\n";
    internal static readonly int MaxCombinedCapturedCharacters = MaxCapturedStdoutCharacters
                                                                + MaxCapturedStderrCharacters
                                                                + (2 * TruncationMarker.Length);

    private const int SigKill = 9;
    private const int InterruptedSystemCall = 4;
    private const int PosixIdTypePid = 1;
    private const int PosixWaitExited = 0x00000004;
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds (5);

    internal readonly record struct RunResult (
        int Code,
        string Output,
        bool StdoutTruncated,
        bool StderrTruncated,
        bool CleanupIncomplete)
    {
        internal bool OutputComplete => !StdoutTruncated && !StderrTruncated && !CleanupIncomplete;
    }

    private readonly record struct CapturedStream (string Text, bool Truncated);

    internal static async Task<(int Code, string Output)> RunAsync (
        string executable,
        IReadOnlyList<string> args,
        Encoding outputEncoding,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        RunResult result = await RunDetailedAsync (executable, args, outputEncoding, timeout, cancellationToken)
                           .ConfigureAwait (false);

        return (result.Code, result.Output);
    }

    internal static Task<RunResult> RunDetailedAsync (
        string executable,
        IReadOnlyList<string> args,
        Encoding outputEncoding,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace (executable);

        if (executable.Contains ('\0') || args.Any (arg => arg.Contains ('\0')))
        {
            throw new ArgumentException ("Process executable and arguments cannot contain NUL characters.");
        }

        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException (nameof (timeout), "The process timeout must be finite and positive.");
        }

        cancellationToken.ThrowIfCancellationRequested ();

        return OperatingSystem.IsWindows ()
                   ? RunWindowsAsync (executable, args, outputEncoding, timeout, cancellationToken)
                   : RunPosixAsync (executable, args, outputEncoding, timeout, cancellationToken, testHooks: null);
    }

    internal static async Task<(int Code, string Output)> RunWithPosixWaitObserverForTestAsync (
        string executable,
        IReadOnlyList<string> args,
        Encoding outputEncoding,
        TimeSpan timeout,
        Action<int> afterWaitWithoutReaping,
        CancellationToken cancellationToken)
    {
        RunResult result = await RunPosixAsync (
                               executable,
                               args,
                               outputEncoding,
                               timeout,
                               cancellationToken,
                               new (afterWaitWithoutReaping, null, null))
                           .ConfigureAwait (false);

        return (result.Code, result.Output);
    }

    internal static async Task<(int Code, string Output)> RunWithPosixLifecycleBarrierForTestAsync (
        string executable,
        IReadOnlyList<string> args,
        Encoding outputEncoding,
        TimeSpan timeout,
        Action<int> afterWaitWithoutReaping,
        Action<bool> terminationDecision,
        CancellationToken cancellationToken)
    {
        RunResult result = await RunPosixAsync (
                               executable,
                               args,
                               outputEncoding,
                               timeout,
                               cancellationToken,
                               new (afterWaitWithoutReaping, terminationDecision, null))
                           .ConfigureAwait (false);

        return (result.Code, result.Output);
    }

    internal static async Task<(int Code, string Output)> RunWithPosixLaunchBarrierForTestAsync (
        string executable,
        IReadOnlyList<string> args,
        Encoding outputEncoding,
        TimeSpan timeout,
        Action afterPipesCreatedBeforeCloseOnExec,
        CancellationToken cancellationToken)
    {
        RunResult result = await RunPosixAsync (
                               executable,
                               args,
                               outputEncoding,
                               timeout,
                               cancellationToken,
                               new (null, null, afterPipesCreatedBeforeCloseOnExec))
                           .ConfigureAwait (false);

        return (result.Code, result.Output);
    }

    internal static string QuoteWindowsArgument (string argument)
    {
        if (argument.Length > 0 && !argument.Any (c => char.IsWhiteSpace (c) || c == '"'))
        {
            return argument;
        }

        StringBuilder quoted = new (argument.Length + 2);
        quoted.Append ('"');
        int backslashes = 0;

        foreach (char c in argument)
        {
            if (c == '\\')
            {
                backslashes++;

                continue;
            }

            if (c == '"')
            {
                quoted.Append ('\\', backslashes * 2 + 1);
                quoted.Append ('"');
                backslashes = 0;

                continue;
            }

            quoted.Append ('\\', backslashes);
            backslashes = 0;
            quoted.Append (c);
        }

        quoted.Append ('\\', backslashes * 2);
        quoted.Append ('"');

        return quoted.ToString ();
    }

    internal static string BuildWindowsCommandLine (string executable, IReadOnlyList<string> args)
        => string.Join (' ', new [] { executable }.Concat (args).Select (QuoteWindowsArgument));

    internal static void WaitForPosixExitWithoutReapingForTest (int pid)
        => WaitForPosixExitWithoutReaping (pid);

    internal static int GetPosixProcessGroupForTest (int pid) => GetProcessGroupFor (pid);

    private static async Task<RunResult> RunPosixAsync (
        string executable,
        IReadOnlyList<string> args,
        Encoding outputEncoding,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        PosixTestHooks? testHooks)
    {
        using PosixChildProcess child = PosixChildProcess.Start (
            executable,
            args,
            outputEncoding,
            testHooks?.AfterPipesCreatedBeforeCloseOnExec);
        int processGroupId = child.ProcessId;
        Task<CapturedStream> stdoutTask = DrainBoundedAsync (child.StandardOutput, MaxCapturedStdoutCharacters);
        Task<CapturedStream> stderrTask = DrainBoundedAsync (child.StandardError, MaxCapturedStderrCharacters);
        Task allOutputTask = Task.WhenAll (stdoutTask, stderrTask);
        PosixProcessLifecycle lifecycle = new (processGroupId, testHooks?.TerminationDecision);
        Task<int> exitTask = Task.Factory.StartNew (
            () => lifecycle.WaitCleanupAndReap (testHooks?.AfterWaitWithoutReaping),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        using CancellationTokenSource deadline = new (timeout);
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource (cancellationToken, deadline.Token);
        try
        {
            int exitCode = await exitTask.WaitAsync (lifetime.Token).ConfigureAwait (false);
            bool drainComplete = await FinishDrainAsync (
                    null,
                    child.StandardOutput,
                    child.StandardError,
                    allOutputTask,
                    CleanupTimeout)
                .ConfigureAwait (false);

            return CombineCompletedOutput (exitCode, stdoutTask, stderrTask, !drainComplete);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            long cleanupStarted = Stopwatch.GetTimestamp ();
            // WaitAsync cancellation can resume this continuation inline on the caller's Cancel
            // stack. Explicitly queue signaling so the lifecycle lock and kill syscalls never
            // block CancellationTokenSource.Cancel, including cancellation from the UI thread.
            Task terminationTask = Task.Run (lifecycle.TerminateIfOwned, CancellationToken.None);
            await WaitBoundedNoThrowAsync (
                    terminationTask,
                    RemainingCleanupTime (cleanupStarted, CleanupTimeout))
                .ConfigureAwait (false);
            await WaitBoundedNoThrowAsync (exitTask, RemainingCleanupTime (cleanupStarted, CleanupTimeout)).ConfigureAwait (false);
            await FinishDrainAsync (
                    null,
                    child.StandardOutput,
                    child.StandardError,
                    allOutputTask,
                    RemainingCleanupTime (cleanupStarted, CleanupTimeout))
                .ConfigureAwait (false);
            cancellationToken.ThrowIfCancellationRequested ();
            throw ProcessTimeout (executable, args, timeout);
        }
        catch
        {
            long cleanupStarted = Stopwatch.GetTimestamp ();
            lifecycle.TerminateIfOwned ();
            await WaitBoundedNoThrowAsync (exitTask, RemainingCleanupTime (cleanupStarted, CleanupTimeout)).ConfigureAwait (false);
            await FinishDrainAsync (
                    null,
                    child.StandardOutput,
                    child.StandardError,
                    allOutputTask,
                    RemainingCleanupTime (cleanupStarted, CleanupTimeout))
                .ConfigureAwait (false);
            throw;
        }
    }

    private static async Task<RunResult> RunWindowsAsync (
        string executable,
        IReadOnlyList<string> args,
        Encoding outputEncoding,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using WindowsChildProcess child = WindowsChildProcess.Start (executable, args, outputEncoding);
        Task<CapturedStream> stdoutTask = DrainBoundedAsync (child.StandardOutput, MaxCapturedStdoutCharacters);
        Task<CapturedStream> stderrTask = DrainBoundedAsync (child.StandardError, MaxCapturedStderrCharacters);
        Task allOutputTask = Task.WhenAll (stdoutTask, stderrTask);
        using CancellationTokenSource deadline = new (timeout);
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource (cancellationToken, deadline.Token);

        try
        {
            await child.Process.WaitForExitAsync (lifetime.Token).ConfigureAwait (false);
            int exitCode = child.Process.ExitCode;
            child.TerminateJob ();
            bool drainComplete = await FinishDrainAsync (
                    child.Process,
                    child.StandardOutput,
                    child.StandardError,
                    allOutputTask,
                    CleanupTimeout)
                .ConfigureAwait (false);

            return CombineCompletedOutput (exitCode, stdoutTask, stderrTask, !drainComplete);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            child.TerminateJob ();
            await FinishDrainAsync (
                    child.Process,
                    child.StandardOutput,
                    child.StandardError,
                    allOutputTask,
                    CleanupTimeout)
                .ConfigureAwait (false);
            cancellationToken.ThrowIfCancellationRequested ();
            throw ProcessTimeout (executable, args, timeout);
        }
        catch
        {
            child.TerminateJob ();
            await FinishDrainAsync (
                    child.Process,
                    child.StandardOutput,
                    child.StandardError,
                    allOutputTask,
                    CleanupTimeout)
                .ConfigureAwait (false);
            throw;
        }
    }

    private static TimeoutException ProcessTimeout (string executable, IReadOnlyList<string> args, TimeSpan timeout)
    {
        string command = args.Count > 0 ? $" {args [0]}" : string.Empty;

        return new ($"Process '{executable}{command}' exceeded its {timeout.TotalSeconds:0.###}-second deadline and was terminated.");
    }

    private static void TerminatePosixGroup (int processGroupId)
    {
        // POSIX_SPAWN_SETPGROUP establishes PGID == PID atomically before exec. Until waitpid runs,
        // the leader remains waitable and that PID/PGID identity cannot be reused. A target that
        // deliberately creates a different process group/session escapes this contract.
        bool safeGroup = processGroupId > 1 && processGroupId != GetProcessGroup ();

        if (safeGroup)
        {
            KillProcess (-processGroupId, SigKill);
        }

        // Also signal the leader directly as a defensive fallback. Never query Process.HasExited
        // here: that can reap the leader and release PGID identity.
        KillProcess (processGroupId, SigKill);
    }

    private static void WaitForPosixExitWithoutReaping (int pid)
    {
        int waitNoWait = OperatingSystem.IsMacOS () ? 0x00000020 : 0x01000000;
        IntPtr info = Marshal.AllocHGlobal (256);

        try
        {
            while (WaitId (PosixIdTypePid, (uint) pid, info, PosixWaitExited | waitNoWait) != 0)
            {
                int error = Marshal.GetLastWin32Error ();

                if (error != InterruptedSystemCall)
                {
                    throw new Win32Exception (error, $"waitid(WEXITED|WNOWAIT) failed for process {pid}.");
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal (info);
        }
    }

    private static int ReapPosixProcess (int pid)
    {
        while (true)
        {
            int result = WaitPid (pid, out int status, 0);

            if (result == pid)
            {
                int signal = status & 0x7f;

                return signal == 0 ? (status >> 8) & 0xff : 128 + signal;
            }

            int error = Marshal.GetLastWin32Error ();

            if (result < 0 && error == InterruptedSystemCall)
            {
                continue;
            }

            throw new Win32Exception (error, $"waitpid failed for contained process {pid} (result {result}).");
        }
    }

    private static async Task<CapturedStream> DrainBoundedAsync (StreamReader reader, int maximumCharacters)
    {
        char [] buffer = ArrayPool<char>.Shared.Rent (8192);

        try
        {
            StringBuilder retained = new (Math.Min (8192, maximumCharacters));
            bool truncated = false;

            while (true)
            {
                int read = await reader.ReadAsync (buffer.AsMemory (0, buffer.Length)).ConfigureAwait (false);

                if (read == 0)
                {
                    break;
                }

                int remaining = maximumCharacters - retained.Length;

                if (remaining > 0)
                {
                    int append = Math.Min (remaining, read);
                    retained.Append (buffer, 0, append);
                    truncated |= append < read;
                }
                else
                {
                    truncated = true;
                }
            }

            if (truncated)
            {
                // The character limit may land between the UTF-16 code units of a supplementary
                // scalar. Never expose a dangling high surrogate in retained process output.
                if (retained.Length > 0 && char.IsHighSurrogate (retained [^1]))
                {
                    retained.Length--;
                }

                retained.Append (TruncationMarker);
            }

            return new (retained.ToString (), truncated);
        }
        finally
        {
            ArrayPool<char>.Shared.Return (buffer);
        }
    }

    internal static async Task<(string Text, bool Truncated)> DrainBoundedForTestAsync (
        StreamReader reader,
        int maximumCharacters)
    {
        CapturedStream captured = await DrainBoundedAsync (reader, maximumCharacters).ConfigureAwait (false);

        return (captured.Text, captured.Truncated);
    }

    internal static Task<bool> FinishDrainForTestAsync (
        StreamReader stdout,
        StreamReader stderr,
        Task outputTask,
        TimeSpan timeout)
        => FinishDrainAsync (null, stdout, stderr, outputTask, timeout);

    private static async Task<bool> FinishDrainAsync (
        Process? process,
        StreamReader stdout,
        StreamReader stderr,
        Task outputTask,
        TimeSpan timeout)
    {
        long started = Stopwatch.GetTimestamp ();

        if (process is not null)
        {
            await WaitBoundedNoThrowAsync (process.WaitForExitAsync (), RemainingCleanupTime (started, timeout)).ConfigureAwait (false);
        }

        await WaitBoundedNoThrowAsync (outputTask, RemainingCleanupTime (started, timeout)).ConfigureAwait (false);

        if (!outputTask.IsCompleted)
        {
            TryDisposeReader (stdout);
            TryDisposeReader (stderr);
            await WaitBoundedNoThrowAsync (outputTask, RemainingCleanupTime (started, timeout)).ConfigureAwait (false);
        }

        return outputTask.IsCompleted;
    }

    private static RunResult CombineCompletedOutput (
        int exitCode,
        Task<CapturedStream> stdoutTask,
        Task<CapturedStream> stderrTask,
        bool cleanupIncomplete)
    {
        CapturedStream stdout = stdoutTask.IsCompletedSuccessfully
                                    ? stdoutTask.Result
                                    : new (DrainIncompleteMarker, false);
        CapturedStream stderr = stderrTask.IsCompletedSuccessfully
                                    ? stderrTask.Result
                                    : new (DrainIncompleteMarker, false);

        return new (
            exitCode,
            string.Concat (stdout.Text, stderr.Text),
            stdout.Truncated,
            stderr.Truncated,
            cleanupIncomplete || !stdoutTask.IsCompletedSuccessfully || !stderrTask.IsCompletedSuccessfully);
    }

    private static TimeSpan RemainingCleanupTime (long started, TimeSpan timeout)
    {
        TimeSpan remaining = timeout - Stopwatch.GetElapsedTime (started);

        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private static void TryDisposeReader (StreamReader reader)
    {
        try
        {
            reader.Dispose ();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private static async Task<bool> WaitBoundedNoThrowAsync (Task task, TimeSpan timeout)
    {
        try
        {
            await task.WaitAsync (timeout).ConfigureAwait (false);

            return true;
        }
        catch (Exception)
        {
            return task.IsCompleted;
        }
    }

    private sealed record PosixTestHooks (
        Action<int>? AfterWaitWithoutReaping,
        Action<bool>? TerminationDecision,
        Action? AfterPipesCreatedBeforeCloseOnExec);

    private sealed class PosixProcessLifecycle
    {
        private readonly object _gate = new ();
        private readonly int _processId;
        private readonly Action<bool>? _terminationDecision;
        private bool _reaped;

        internal PosixProcessLifecycle (int processId, Action<bool>? terminationDecision)
        {
            _processId = processId;
            _terminationDecision = terminationDecision;
        }

        internal int WaitCleanupAndReap (Action<int>? afterWaitWithoutReaping)
        {
            WaitForPosixExitWithoutReaping (_processId);

            lock (_gate)
            {
                Exception? observerFailure = null;

                try
                {
                    afterWaitWithoutReaping?.Invoke (_processId);
                }
                catch (Exception ex)
                {
                    observerFailure = ex;
                }

                // Keep waitid, both group-kill attempts, and waitpid under one ownership lock. The
                // posix_spawn child is not in the runtime's child table, and cancellation must take
                // this same lock before signaling, so no signal can follow the reaped transition.
                TerminatePosixGroup (_processId);
                ObserveTerminationDecision (willSignal: true);
                int exitCode;

                try
                {
                    exitCode = ReapPosixProcess (_processId);
                }
                finally
                {
                    _reaped = true;
                }

                if (observerFailure is not null)
                {
                    throw observerFailure;
                }

                return exitCode;
            }
        }

        internal void TerminateIfOwned ()
        {
            lock (_gate)
            {
                if (_reaped)
                {
                    ObserveTerminationDecision (willSignal: false);

                    return;
                }

                TerminatePosixGroup (_processId);
                ObserveTerminationDecision (willSignal: true);
            }
        }

        private void ObserveTerminationDecision (bool willSignal)
        {
            try
            {
                _terminationDecision?.Invoke (willSignal);
            }
            catch
            {
                // Test instrumentation must never alter process ownership or cleanup.
            }
        }
    }

    private sealed class PosixChildProcess : IDisposable
    {
        private static readonly object LaunchGate = new ();
        private const int FileDescriptorCloseOnExec = 1;
        private const int DuplicateFileDescriptor = 0;
        private const int DuplicateFileDescriptorCloseOnExecLinux = 1030;
        private const int SetFileDescriptorFlags = 2;
        private const int FirstNonStandardDescriptor = 3;
        private const int OpenCloseOnExecLinux = 0x00080000;
        private const short PosixSpawnSetProcessGroup = 0x0002;
        // Darwin stores an opaque pointer here; glibc/musl store a small opaque structure. This
        // allocation is intentionally larger than the definitions on supported macOS/Linux ABIs.
        private const int FileActionsStorageBytes = 256;
        private const int SpawnAttributesStorageBytes = 512;

        private PosixChildProcess (int processId, StreamReader stdout, StreamReader stderr)
        {
            ProcessId = processId;
            StandardOutput = stdout;
            StandardError = stderr;
        }

        internal int ProcessId { get; }
        internal StreamReader StandardOutput { get; }
        internal StreamReader StandardError { get; }

        internal static PosixChildProcess Start (
            string executable,
            IReadOnlyList<string> args,
            Encoding encoding,
            Action? afterPipesCreatedBeforeCloseOnExec)
        {
            lock (LaunchGate)
            {
                return StartLocked (executable, args, encoding, afterPipesCreatedBeforeCloseOnExec);
            }
        }

        private static PosixChildProcess StartLocked (
            string executable,
            IReadOnlyList<string> args,
            Encoding encoding,
            Action? afterPipesCreatedBeforeCloseOnExec)
        {
            SafeFileHandle? stdoutRead = null;
            SafeFileHandle? stdoutWrite = null;
            SafeFileHandle? stderrRead = null;
            SafeFileHandle? stderrWrite = null;
            StreamReader? stdoutReader = null;
            StreamReader? stderrReader = null;
            IntPtr fileActions = IntPtr.Zero;
            bool fileActionsInitialized = false;
            IntPtr spawnAttributes = IntPtr.Zero;
            bool spawnAttributesInitialized = false;
            int processId = 0;

            try
            {
                CreatePipe (out stdoutRead, out stdoutWrite);
                CreatePipe (out stderrRead, out stderrWrite);
                afterPipesCreatedBeforeCloseOnExec?.Invoke ();
                SetCloseOnExec (stdoutRead);
                SetCloseOnExec (stdoutWrite);
                SetCloseOnExec (stderrRead);
                SetCloseOnExec (stderrWrite);
                fileActions = Marshal.AllocHGlobal (FileActionsStorageBytes);
                int actionError = PosixSpawnFileActionsInit (fileActions);

                if (actionError != 0)
                {
                    throw new Win32Exception (actionError, "Could not initialize POSIX process file actions.");
                }

                fileActionsInitialized = true;
                AddCloseAction (fileActions, stdoutRead);
                AddCloseAction (fileActions, stderrRead);
                AddDupAction (fileActions, stdoutWrite, 1);
                AddDupAction (fileActions, stderrWrite, 2);
                AddCloseActionUnlessSame (fileActions, stdoutWrite, 1);
                AddCloseActionUnlessSame (fileActions, stderrWrite, 2);
                spawnAttributes = Marshal.AllocHGlobal (SpawnAttributesStorageBytes);
                int attributeError = PosixSpawnAttributesInit (spawnAttributes);

                if (attributeError != 0)
                {
                    throw new Win32Exception (attributeError, "Could not initialize POSIX process attributes.");
                }

                spawnAttributesInitialized = true;
                ThrowIfPosixError (
                    PosixSpawnAttributesSetProcessGroup (spawnAttributes, 0),
                    "Could not configure a new POSIX process group.");
                ThrowIfPosixError (
                    PosixSpawnAttributesSetFlags (spawnAttributes, PosixSpawnSetProcessGroup),
                    "Could not enable POSIX process-group containment.");

                using Utf8StringVector argv = new (new [] { executable }.Concat (args));
                using Utf8StringVector environment = new (
                    Environment.GetEnvironmentVariables ()
                               .Cast<DictionaryEntry> ()
                               .Select (entry => $"{entry.Key}={entry.Value}"));
                int spawnError = PosixSpawnP (
                    out processId,
                    executable,
                    fileActions,
                    spawnAttributes,
                    argv.Pointer,
                    environment.Pointer);

                if (spawnError != 0)
                {
                    throw new Win32Exception (spawnError, $"Could not create contained process '{executable}'.");
                }

                stdoutWrite.Dispose ();
                stdoutWrite = null;
                stderrWrite.Dispose ();
                stderrWrite = null;
                stdoutReader = new (new FileStream (stdoutRead, FileAccess.Read, 8192, isAsync: false), encoding, false, 8192);
                stdoutRead = null;
                stderrReader = new (new FileStream (stderrRead, FileAccess.Read, 8192, isAsync: false), encoding, false, 8192);
                stderrRead = null;
                PosixChildProcess result = new (processId, stdoutReader, stderrReader);
                stdoutReader = null;
                stderrReader = null;

                return result;
            }
            catch
            {
                if (processId > 0)
                {
                    TerminatePosixGroup (processId);
                    ReapPosixProcess (processId);
                }

                stdoutReader?.Dispose ();
                stderrReader?.Dispose ();
                throw;
            }
            finally
            {
                stdoutRead?.Dispose ();
                stdoutWrite?.Dispose ();
                stderrRead?.Dispose ();
                stderrWrite?.Dispose ();

                if (fileActionsInitialized)
                {
                    PosixSpawnFileActionsDestroy (fileActions);
                }

                if (spawnAttributesInitialized)
                {
                    PosixSpawnAttributesDestroy (spawnAttributes);
                }

                if (fileActions != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal (fileActions);
                }

                if (spawnAttributes != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal (spawnAttributes);
                }
            }
        }

        public void Dispose ()
        {
            StandardOutput.Dispose ();
            StandardError.Dispose ();
        }

        private static void CreatePipe (out SafeFileHandle read, out SafeFileHandle write)
        {
            int [] descriptors = new int [2];

            int pipeResult = OperatingSystem.IsLinux ()
                                 ? Pipe2 (descriptors, OpenCloseOnExecLinux)
                                 : Pipe (descriptors);

            if (pipeResult != 0)
            {
                throw new Win32Exception (Marshal.GetLastWin32Error (), "Could not create a redirected POSIX process pipe.");
            }

            read = new ((IntPtr) descriptors [0], ownsHandle: true);
            write = new ((IntPtr) descriptors [1], ownsHandle: true);

            try
            {
                MoveAboveStandardDescriptors (ref read);
                MoveAboveStandardDescriptors (ref write);
            }
            catch
            {
                read.Dispose ();
                write.Dispose ();
                throw;
            }
        }

        private static void MoveAboveStandardDescriptors (ref SafeFileHandle handle)
        {
            if ((long) handle.DangerousGetHandle () >= FirstNonStandardDescriptor)
            {
                return;
            }

            int duplicateCommand = OperatingSystem.IsLinux ()
                                       ? DuplicateFileDescriptorCloseOnExecLinux
                                       : DuplicateFileDescriptor;
            int duplicate = Fcntl (handle, duplicateCommand, FirstNonStandardDescriptor);

            if (duplicate < 0)
            {
                throw new Win32Exception (Marshal.GetLastWin32Error (), "Could not relocate a POSIX process pipe descriptor.");
            }

            SafeFileHandle relocated = new ((IntPtr) duplicate, ownsHandle: true);
            handle.Dispose ();
            handle = relocated;
        }

        private static void SetCloseOnExec (SafeFileHandle handle)
        {
            if (Fcntl (handle, SetFileDescriptorFlags, FileDescriptorCloseOnExec) != 0)
            {
                throw new Win32Exception (Marshal.GetLastWin32Error (), "Could not protect a POSIX process pipe from inheritance.");
            }
        }

        private static void AddCloseAction (IntPtr actions, SafeFileHandle handle)
            => ThrowIfPosixError (
                PosixSpawnFileActionsAddClose (actions, checked ((int) handle.DangerousGetHandle ())),
                "Could not configure a POSIX pipe close action.");

        private static void AddCloseActionUnlessSame (IntPtr actions, SafeFileHandle handle, int descriptor)
        {
            if (handle.DangerousGetHandle () != (IntPtr) descriptor)
            {
                AddCloseAction (actions, handle);
            }
        }

        private static void AddDupAction (IntPtr actions, SafeFileHandle handle, int descriptor)
            => ThrowIfPosixError (
                PosixSpawnFileActionsAddDup2 (actions, checked ((int) handle.DangerousGetHandle ()), descriptor),
                "Could not configure a POSIX pipe duplication action.");

        private static void ThrowIfPosixError (int error, string message)
        {
            if (error != 0)
            {
                throw new Win32Exception (error, message);
            }
        }
    }

    private sealed class Utf8StringVector : IDisposable
    {
        private readonly IntPtr [] _strings;

        internal Utf8StringVector (IEnumerable<string> values)
        {
            string [] source = [.. values];
            _strings = new IntPtr [source.Length];
            Pointer = Marshal.AllocHGlobal ((source.Length + 1) * IntPtr.Size);

            try
            {
                for (int i = 0; i < source.Length; i++)
                {
                    _strings [i] = Marshal.StringToCoTaskMemUTF8 (source [i]);
                    Marshal.WriteIntPtr (Pointer, i * IntPtr.Size, _strings [i]);
                }

                Marshal.WriteIntPtr (Pointer, source.Length * IntPtr.Size, IntPtr.Zero);
            }
            catch
            {
                Dispose ();
                throw;
            }
        }

        internal IntPtr Pointer { get; private set; }

        public void Dispose ()
        {
            foreach (IntPtr value in _strings)
            {
                if (value != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem (value);
                }
            }

            if (Pointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal (Pointer);
                Pointer = IntPtr.Zero;
            }
        }
    }

    private sealed class WindowsChildProcess : IDisposable
    {
        private const uint CreateSuspended = 0x00000004;
        private const uint CreateNoWindow = 0x08000000;
        private const uint ExtendedStartupInfoPresent = 0x00080000;
        private const uint StartfUseStdHandles = 0x00000100;
        private const uint HandleFlagInherit = 0x00000001;
        private const nuint ProcThreadAttributeHandleList = 0x00020002;
        private const uint DuplicateSameAccess = 0x00000002;
        private const uint GenericRead = 0x80000000;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint OpenExisting = 3;
        private const int StdInputHandle = -10;

        private readonly WindowsJob _job;
        // Process owns this SafeHandle. Retaining the reference documents and guarantees that the
        // managed handle acquired while the native process handle was live stays rooted.
        private readonly SafeProcessHandle _managedProcessHandle;
        private bool _terminated;

        private WindowsChildProcess (
            Process process,
            SafeProcessHandle managedProcessHandle,
            StreamReader stdout,
            StreamReader stderr,
            WindowsJob job)
        {
            Process = process;
            _managedProcessHandle = managedProcessHandle;
            StandardOutput = stdout;
            StandardError = stderr;
            _job = job;
        }

        internal Process Process { get; }
        internal StreamReader StandardOutput { get; }
        internal StreamReader StandardError { get; }

        internal static WindowsChildProcess Start (string executable, IReadOnlyList<string> args, Encoding encoding)
        {
            string resolvedExecutable = ResolveWindowsExecutable (executable);
            string commandLine = BuildWindowsCommandLine (resolvedExecutable, args);
            SecurityAttributes inheritable = new ()
            {
                Length = Marshal.SizeOf<SecurityAttributes> (),
                InheritHandle = true
            };
            WindowsJob job = new ();
            SafeFileHandle? stdoutRead = null;
            SafeFileHandle? stdoutWrite = null;
            SafeFileHandle? stderrRead = null;
            SafeFileHandle? stderrWrite = null;
            SafeFileHandle? stdin = null;
            SafeFileHandle? nativeProcess = null;
            SafeFileHandle? nativeThread = null;
            IntPtr attributeList = IntPtr.Zero;
            bool attributeListInitialized = false;
            IntPtr inheritedHandles = IntPtr.Zero;
            Process? process = null;
            SafeProcessHandle? managedProcessHandle = null;
            StreamReader? stdoutReader = null;
            StreamReader? stderrReader = null;

            try
            {
                CreatePipePair (ref inheritable, out stdoutRead, out stdoutWrite);
                CreatePipePair (ref inheritable, out stderrRead, out stderrWrite);
                stdin = DuplicateOrOpenStdin (ref inheritable);

                nuint attributeBytes = 0;
                InitializeProcThreadAttributeList (IntPtr.Zero, 1, 0, ref attributeBytes);
                attributeList = Marshal.AllocHGlobal ((nint) attributeBytes);

                if (!InitializeProcThreadAttributeList (attributeList, 1, 0, ref attributeBytes))
                {
                    throw new Win32Exception (Marshal.GetLastWin32Error (), "Could not initialize the process handle allow-list.");
                }

                attributeListInitialized = true;

                IntPtr [] handles = [stdin.DangerousGetHandle (), stdoutWrite.DangerousGetHandle (), stderrWrite.DangerousGetHandle ()];
                inheritedHandles = Marshal.AllocHGlobal (handles.Length * IntPtr.Size);

                for (int i = 0; i < handles.Length; i++)
                {
                    Marshal.WriteIntPtr (inheritedHandles, i * IntPtr.Size, handles [i]);
                }

                if (!UpdateProcThreadAttribute (
                        attributeList,
                        0,
                        ProcThreadAttributeHandleList,
                        inheritedHandles,
                        (nuint) (handles.Length * IntPtr.Size),
                        IntPtr.Zero,
                        IntPtr.Zero))
                {
                    throw new Win32Exception (Marshal.GetLastWin32Error (), "Could not configure inherited process handles.");
                }

                StartupInfoEx startup = new ();
                startup.StartupInfo.Size = Marshal.SizeOf<StartupInfoEx> ();
                startup.StartupInfo.Flags = StartfUseStdHandles;
                startup.StartupInfo.StandardInput = stdin.DangerousGetHandle ();
                startup.StartupInfo.StandardOutput = stdoutWrite.DangerousGetHandle ();
                startup.StartupInfo.StandardError = stderrWrite.DangerousGetHandle ();
                startup.AttributeList = attributeList;
                StringBuilder mutableCommandLine = new (commandLine);

                if (!CreateProcess (
                        resolvedExecutable,
                        mutableCommandLine,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        true,
                        CreateSuspended | CreateNoWindow | ExtendedStartupInfoPresent,
                        IntPtr.Zero,
                        null,
                        ref startup,
                        out ProcessInformation processInformation))
                {
                    throw new Win32Exception (Marshal.GetLastWin32Error (), $"Could not create contained process '{resolvedExecutable}'.");
                }

                nativeProcess = new (processInformation.Process, ownsHandle: true);
                nativeThread = new (processInformation.Thread, ownsHandle: true);
                job.Assign (nativeProcess);
                process = Process.GetProcessById ((int) processInformation.ProcessId);
                managedProcessHandle = process.SafeHandle;

                if (managedProcessHandle.IsInvalid || managedProcessHandle.IsClosed)
                {
                    throw new InvalidOperationException ("Could not retain the contained process handle.");
                }

                if (ResumeThread (nativeThread) == uint.MaxValue)
                {
                    throw new Win32Exception (Marshal.GetLastWin32Error (), "Could not resume the contained process.");
                }

                stdoutWrite.Dispose ();
                stdoutWrite = null;
                stderrWrite.Dispose ();
                stderrWrite = null;
                stdoutReader = new (new FileStream (stdoutRead, FileAccess.Read, 8192, isAsync: false), encoding, false, 8192);
                stdoutRead = null;
                stderrReader = new (new FileStream (stderrRead, FileAccess.Read, 8192, isAsync: false), encoding, false, 8192);
                stderrRead = null;
                WindowsChildProcess result = new (process, managedProcessHandle, stdoutReader, stderrReader, job);
                process = null;
                managedProcessHandle = null;
                stdoutReader = null;
                stderrReader = null;

                return result;
            }
            catch
            {
                if (nativeProcess is not null && !nativeProcess.IsInvalid)
                {
                    TerminateProcess (nativeProcess, 127);
                }

                job.Dispose ();
                process?.Dispose ();
                stdoutReader?.Dispose ();
                stderrReader?.Dispose ();
                throw;
            }
            finally
            {
                nativeThread?.Dispose ();
                nativeProcess?.Dispose ();
                stdin?.Dispose ();
                stdoutRead?.Dispose ();
                stdoutWrite?.Dispose ();
                stderrRead?.Dispose ();
                stderrWrite?.Dispose ();

                if (attributeListInitialized)
                {
                    DeleteProcThreadAttributeList (attributeList);
                }

                if (attributeList != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal (attributeList);
                }

                if (inheritedHandles != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal (inheritedHandles);
                }
            }
        }

        internal void TerminateJob ()
        {
            if (_terminated)
            {
                return;
            }

            _terminated = true;
            _job.Dispose ();
        }

        public void Dispose ()
        {
            TerminateJob ();
            StandardOutput.Dispose ();
            StandardError.Dispose ();
            Process.Dispose ();
            GC.KeepAlive (_managedProcessHandle);
        }

        private static void CreatePipePair (
            ref SecurityAttributes attributes,
            out SafeFileHandle read,
            out SafeFileHandle write)
        {
            if (!CreatePipe (out IntPtr readHandle, out IntPtr writeHandle, ref attributes, 0))
            {
                throw new Win32Exception (Marshal.GetLastWin32Error (), "Could not create a redirected process pipe.");
            }

            read = new (readHandle, ownsHandle: true);
            write = new (writeHandle, ownsHandle: true);

            if (!SetHandleInformation (read, HandleFlagInherit, 0))
            {
                read.Dispose ();
                write.Dispose ();
                throw new Win32Exception (Marshal.GetLastWin32Error (), "Could not protect the parent side of a process pipe.");
            }
        }

        private static SafeFileHandle DuplicateOrOpenStdin (ref SecurityAttributes attributes)
        {
            IntPtr current = GetCurrentProcess ();
            IntPtr source = GetStdHandle (StdInputHandle);

            if (source != IntPtr.Zero
                && source != new IntPtr (-1)
                && DuplicateHandle (current, source, current, out IntPtr duplicate, 0, true, DuplicateSameAccess))
            {
                return new (duplicate, ownsHandle: true);
            }

            SafeFileHandle nul = CreateFile (
                "NUL",
                GenericRead,
                FileShareRead | FileShareWrite,
                ref attributes,
                OpenExisting,
                0,
                IntPtr.Zero);

            if (nul.IsInvalid)
            {
                throw new Win32Exception (Marshal.GetLastWin32Error (), "Could not provide stdin to the contained process.");
            }

            return nul;
        }

        private static string ResolveWindowsExecutable (string executable)
        {
            if (executable.Contains (Path.DirectorySeparatorChar) || executable.Contains (Path.AltDirectorySeparatorChar))
            {
                string fullPath = Path.GetFullPath (executable);

                if (!File.Exists (fullPath))
                {
                    throw new Win32Exception (2, $"Executable '{executable}' was not found.");
                }

                return fullPath;
            }

            string [] names = Path.HasExtension (executable) ? [executable] : [executable, executable + ".exe"];
            string? pathValue = Environment.GetEnvironmentVariable ("PATH");

            foreach (string directoryValue in (pathValue ?? string.Empty).Split (Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string directory = directoryValue.Trim ().Trim ('"');

                if (directory.Length == 0)
                {
                    continue;
                }

                foreach (string name in names)
                {
                    try
                    {
                        string candidate = Path.GetFullPath (Path.Combine (directory, name));

                        if (File.Exists (candidate))
                        {
                            return candidate;
                        }
                    }
                    catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                    {
                    }
                }
            }

            throw new Win32Exception (2, $"Executable '{executable}' was not found on PATH.");
        }

        [DllImport ("kernel32.dll", SetLastError = true)]
        [return: MarshalAs (UnmanagedType.Bool)]
        private static extern bool CreatePipe (out IntPtr readPipe, out IntPtr writePipe, ref SecurityAttributes attributes, uint size);

        [DllImport ("kernel32.dll", SetLastError = true)]
        [return: MarshalAs (UnmanagedType.Bool)]
        private static extern bool SetHandleInformation (SafeFileHandle handle, uint mask, uint flags);

        [DllImport ("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess ();

        [DllImport ("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle (int standardHandle);

        [DllImport ("kernel32.dll", SetLastError = true)]
        [return: MarshalAs (UnmanagedType.Bool)]
        private static extern bool DuplicateHandle (
            IntPtr sourceProcess,
            IntPtr sourceHandle,
            IntPtr targetProcess,
            out IntPtr targetHandle,
            uint desiredAccess,
            [MarshalAs (UnmanagedType.Bool)] bool inheritHandle,
            uint options);

        [DllImport ("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile (
            string fileName,
            uint desiredAccess,
            uint shareMode,
            ref SecurityAttributes securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport ("kernel32.dll", SetLastError = true)]
        [return: MarshalAs (UnmanagedType.Bool)]
        private static extern bool InitializeProcThreadAttributeList (
            IntPtr attributeList,
            int attributeCount,
            int flags,
            ref nuint size);

        [DllImport ("kernel32.dll", SetLastError = true)]
        [return: MarshalAs (UnmanagedType.Bool)]
        private static extern bool UpdateProcThreadAttribute (
            IntPtr attributeList,
            uint flags,
            nuint attribute,
            IntPtr value,
            nuint size,
            IntPtr previousValue,
            IntPtr returnSize);

        [DllImport ("kernel32.dll")]
        private static extern void DeleteProcThreadAttributeList (IntPtr attributeList);

        [DllImport ("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs (UnmanagedType.Bool)]
        private static extern bool CreateProcess (
            string applicationName,
            StringBuilder commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            [MarshalAs (UnmanagedType.Bool)] bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string? currentDirectory,
            ref StartupInfoEx startupInfo,
            out ProcessInformation processInformation);

        [DllImport ("kernel32.dll", SetLastError = true)]
        private static extern uint ResumeThread (SafeFileHandle thread);

        [DllImport ("kernel32.dll", SetLastError = true)]
        [return: MarshalAs (UnmanagedType.Bool)]
        private static extern bool TerminateProcess (SafeFileHandle process, uint exitCode);

        [StructLayout (LayoutKind.Sequential)]
        private struct SecurityAttributes
        {
            public int Length;
            public IntPtr SecurityDescriptor;

            [MarshalAs (UnmanagedType.Bool)]
            public bool InheritHandle;
        }

        [StructLayout (LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int Size;
            public string? Reserved;
            public string? Desktop;
            public string? Title;
            public uint X;
            public uint Y;
            public uint XSize;
            public uint YSize;
            public uint XCountChars;
            public uint YCountChars;
            public uint FillAttribute;
            public uint Flags;
            public ushort ShowWindow;
            public ushort Reserved2Bytes;
            public IntPtr Reserved2;
            public IntPtr StandardInput;
            public IntPtr StandardOutput;
            public IntPtr StandardError;
        }

        [StructLayout (LayoutKind.Sequential)]
        private struct StartupInfoEx
        {
            public StartupInfo StartupInfo;
            public IntPtr AttributeList;
        }

        [StructLayout (LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            public IntPtr Process;
            public IntPtr Thread;
            public uint ProcessId;
            public uint ThreadId;
        }
    }

    private sealed class WindowsJob : IDisposable
    {
        private const uint JobObjectLimitKillOnJobClose = 0x00002000;
        private const int JobObjectExtendedLimitInformationClass = 9;
        private readonly SafeFileHandle _handle;

        internal WindowsJob ()
        {
            _handle = CreateJobObject (IntPtr.Zero, null);

            if (_handle.IsInvalid)
            {
                throw new Win32Exception (Marshal.GetLastWin32Error (), "Could not create a process-containment Job Object.");
            }

            JobObjectExtendedLimitInformation limits = new ();
            limits.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;

            if (!SetInformationJobObject (
                    _handle,
                    JobObjectExtendedLimitInformationClass,
                    ref limits,
                    (uint) Marshal.SizeOf<JobObjectExtendedLimitInformation> ()))
            {
                int error = Marshal.GetLastWin32Error ();
                _handle.Dispose ();
                throw new Win32Exception (error, "Could not configure process-tree termination for the Job Object.");
            }
        }

        internal void Assign (SafeFileHandle process)
        {
            if (!AssignProcessToJobObject (_handle, process))
            {
                throw new Win32Exception (Marshal.GetLastWin32Error (), "Could not assign the suspended process to its Job Object.");
            }
        }

        public void Dispose () => _handle.Dispose ();

        [DllImport ("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateJobObject (IntPtr jobAttributes, string? name);

        [DllImport ("kernel32.dll", SetLastError = true)]
        [return: MarshalAs (UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject (
            SafeFileHandle job,
            int informationClass,
            ref JobObjectExtendedLimitInformation information,
            uint informationLength);

        [DllImport ("kernel32.dll", SetLastError = true)]
        [return: MarshalAs (UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject (SafeFileHandle job, SafeFileHandle process);

        [StructLayout (LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout (LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout (LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }
    }

    [DllImport ("libc", EntryPoint = "getpgrp")]
    private static extern int GetProcessGroup ();

    [DllImport ("libc", EntryPoint = "getpgid", SetLastError = true)]
    private static extern int GetProcessGroupFor (int pid);

    [DllImport ("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int KillProcess (int pid, int signal);

    [DllImport ("libc", EntryPoint = "waitid", SetLastError = true)]
    private static extern int WaitId (int idType, uint id, IntPtr info, int options);

    [DllImport ("libc", EntryPoint = "waitpid", SetLastError = true)]
    private static extern int WaitPid (int pid, out int status, int options);

    [DllImport ("libc", EntryPoint = "pipe", SetLastError = true)]
    private static extern int Pipe ([Out] int [] descriptors);

    [DllImport ("libc", EntryPoint = "pipe2", SetLastError = true)]
    private static extern int Pipe2 ([Out] int [] descriptors, int flags);

    [DllImport ("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int Fcntl (SafeFileHandle descriptor, int command, int argument);

    [DllImport ("libc", EntryPoint = "posix_spawn_file_actions_init")]
    private static extern int PosixSpawnFileActionsInit (IntPtr actions);

    [DllImport ("libc", EntryPoint = "posix_spawn_file_actions_destroy")]
    private static extern int PosixSpawnFileActionsDestroy (IntPtr actions);

    [DllImport ("libc", EntryPoint = "posix_spawn_file_actions_addclose")]
    private static extern int PosixSpawnFileActionsAddClose (IntPtr actions, int descriptor);

    [DllImport ("libc", EntryPoint = "posix_spawn_file_actions_adddup2")]
    private static extern int PosixSpawnFileActionsAddDup2 (IntPtr actions, int descriptor, int newDescriptor);

    [DllImport ("libc", EntryPoint = "posix_spawnattr_init")]
    private static extern int PosixSpawnAttributesInit (IntPtr attributes);

    [DllImport ("libc", EntryPoint = "posix_spawnattr_destroy")]
    private static extern int PosixSpawnAttributesDestroy (IntPtr attributes);

    [DllImport ("libc", EntryPoint = "posix_spawnattr_setflags")]
    private static extern int PosixSpawnAttributesSetFlags (IntPtr attributes, short flags);

    [DllImport ("libc", EntryPoint = "posix_spawnattr_setpgroup")]
    private static extern int PosixSpawnAttributesSetProcessGroup (IntPtr attributes, int processGroup);

    [DllImport ("libc", EntryPoint = "posix_spawnp")]
    private static extern int PosixSpawnP (
        out int pid,
        [MarshalAs (UnmanagedType.LPUTF8Str)] string path,
        IntPtr fileActions,
        IntPtr attributes,
        IntPtr arguments,
        IntPtr environment);
}
