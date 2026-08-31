using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WingetTuiSharp;

/// <summary>
/// Runs a redirected child process with a finite lifetime and bounded output retention.
/// The pipes are always drained concurrently, even after their capture limits are reached.
/// </summary>
internal static class ProcessRunner
{
    internal const int MaxCapturedCharactersPerStream = 1024 * 1024;
    internal const string TruncationMarker = "\n...[output truncated]...\n";
    internal static readonly int MaxCombinedCapturedCharacters = 2 * (MaxCapturedCharactersPerStream + TruncationMarker.Length);

    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds (5);

    internal static async Task<(int Code, string Output)> RunAsync (
        string executable,
        IReadOnlyList<string> args,
        Encoding outputEncoding,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace (executable);

        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException (nameof (timeout), "The process timeout must be finite and positive.");
        }

        cancellationToken.ThrowIfCancellationRequested ();

        ProcessStartInfo startInfo = new ()
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = outputEncoding,
            StandardErrorEncoding = outputEncoding
        };

        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add (arg);
        }

        using Process process = new () { StartInfo = startInfo };
        using ProcessContainment containment = ProcessContainment.Create ();

        try
        {
            process.Start ();
            containment.Attach (process);
        }
        catch
        {
            TryKillDirectProcess (process);
            throw;
        }

        Task<string> stdoutTask = DrainBoundedAsync (process.StandardOutput);
        Task<string> stderrTask = DrainBoundedAsync (process.StandardError);
        Task allOutputTask = Task.WhenAll (stdoutTask, stderrTask);

        using CancellationTokenSource deadline = new (timeout);
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource (
            cancellationToken,
            deadline.Token);

        try
        {
            await process.WaitForExitAsync (lifetime.Token).ConfigureAwait (false);
            await allOutputTask.WaitAsync (lifetime.Token).ConfigureAwait (false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            await TerminateAndDrainAsync (process, containment, allOutputTask).ConfigureAwait (false);

            // A caller cancellation remains cancellation; an internal deadline is a diagnosable
            // timeout. Check the caller first in case both tokens raced.
            cancellationToken.ThrowIfCancellationRequested ();
            string command = args.Count > 0 ? $" {args [0]}" : string.Empty;
            throw new TimeoutException ($"Process '{executable}{command}' exceeded its {timeout.TotalSeconds:0.###}-second deadline and was terminated.");
        }

        string stdout = await stdoutTask.ConfigureAwait (false);
        string stderr = await stderrTask.ConfigureAwait (false);
        await containment.CompleteAsync ().ConfigureAwait (false);

        // Each component is bounded, so this final allocation is capped by
        // MaxCombinedCapturedCharacters.
        return (process.ExitCode, string.Concat (stdout, stderr));
    }

    private static async Task<string> DrainBoundedAsync (StreamReader reader)
    {
        char [] buffer = ArrayPool<char>.Shared.Rent (8192);

        try
        {
            StringBuilder retained = new (Math.Min (8192, MaxCapturedCharactersPerStream));
            bool truncated = false;

            while (true)
            {
                int read = await reader.ReadAsync (buffer.AsMemory (0, buffer.Length)).ConfigureAwait (false);

                if (read == 0)
                {
                    break;
                }

                int remaining = MaxCapturedCharactersPerStream - retained.Length;

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
                retained.Append (TruncationMarker);
            }

            return retained.ToString ();
        }
        finally
        {
            ArrayPool<char>.Shared.Return (buffer);
        }
    }

    private static async Task TerminateAndDrainAsync (Process process, ProcessContainment containment, Task outputTask)
    {
        containment.TerminateRemaining ();
        TryKillDirectProcess (process);

        await WaitBoundedNoThrowAsync (process.WaitForExitAsync (), CleanupTimeout).ConfigureAwait (false);

        if (!await WaitBoundedNoThrowAsync (outputTask, CleanupTimeout).ConfigureAwait (false))
        {
            // A descendant could keep inherited pipe handles open after the direct child exits.
            // Close our readers after the bounded drain so their tasks cannot remain rooted.
            try
            {
                process.StandardOutput.Dispose ();
                process.StandardError.Dispose ();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
            }

            await WaitBoundedNoThrowAsync (outputTask, CleanupTimeout).ConfigureAwait (false);
        }

        await containment.CompleteAsync ().ConfigureAwait (false);
    }

    private static void TryKillDirectProcess (Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill (entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // It exited between HasExited and Kill.
        }
        catch (Exception ex) when (ex is Win32Exception or NotSupportedException)
        {
            // A platform can fail while enumerating descendants. Still terminate the direct
            // child so cancellation never degrades into leaving the command itself running.
            try
            {
                if (!process.HasExited)
                {
                    process.Kill ();
                }
            }
            catch (Exception fallbackEx) when (fallbackEx is InvalidOperationException
                                               or Win32Exception
                                               or NotSupportedException)
            {
            }
        }
    }

    private static async Task<bool> WaitBoundedNoThrowAsync (Task task, TimeSpan timeout)
    {
        try
        {
            await task.WaitAsync (timeout).ConfigureAwait (false);

            return true;
        }
        catch (Exception ex) when (ex is TimeoutException
                                   or OperationCanceledException
                                   or IOException
                                   or ObjectDisposedException
                                   or InvalidOperationException)
        {
            // Cleanup is best-effort and bounded. Kill(entireProcessTree: true) was already
            // issued; don't replace the original cancellation/timeout with a drain failure.
            return task.IsCompleted;
        }
    }

    /// <summary>
    /// Keeps descendants addressable after their immediate parent exits. Windows uses a Job
    /// Object with kill-on-close, which is inherited by descendants and is the production
    /// containment boundary. Assignment happens immediately after <see cref="Process.Start()"/>;
    /// its API has no suspended-start hook, so a process that explicitly breaks away or creates
    /// and detaches a child before assignment can escape the Windows job. Linux/macOS likewise do
    /// not expose atomic process-group assignment through <see cref="Process"/>, so those
    /// development platforms retain identity-checked descendant snapshots while the parent is
    /// alive. A process that forks and exits before the first snapshot can escape on POSIX.
    /// Cooperative winget processes and adversarial children that remain visible for one
    /// scheduling quantum are contained on every supported platform.
    /// </summary>
    private sealed class ProcessContainment : IDisposable
    {
        private const uint JobObjectLimitKillOnJobClose = 0x00002000;
        private const int JobObjectExtendedLimitInformationClass = 9;
        private const uint ProcAllPids = 1;
        private const int ProcPidTbsdInfo = 3;

        private readonly object _gate = new ();
        private readonly Dictionary<int, DateTime> _trackedDescendants = [];
        private readonly CancellationTokenSource _trackingStop = new ();
        private SafeFileHandle? _job;
        private Task? _trackingTask;
        private int _rootPid;
        private DateTime _rootStarted;
        private bool _completed;

        private ProcessContainment ()
        {
            if (!OperatingSystem.IsWindows ())
            {
                return;
            }

            _job = CreateJobObject (IntPtr.Zero, null);

            if (_job.IsInvalid)
            {
                throw new Win32Exception (Marshal.GetLastWin32Error (), "Could not create a process-containment Job Object.");
            }

            JobObjectExtendedLimitInformation limits = new ();
            limits.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;

            if (!SetInformationJobObject (
                    _job,
                    JobObjectExtendedLimitInformationClass,
                    ref limits,
                    (uint) Marshal.SizeOf<JobObjectExtendedLimitInformation> ()))
            {
                int error = Marshal.GetLastWin32Error ();
                _job.Dispose ();
                throw new Win32Exception (error, "Could not configure process-tree termination for the Job Object.");
            }
        }

        internal static ProcessContainment Create () => new ();

        internal void Attach (Process process)
        {
            _rootPid = process.Id;
            _rootStarted = process.StartTime.ToUniversalTime ();

            if (_job is not null)
            {
                if (!AssignProcessToJobObject (_job, process.Handle))
                {
                    throw new Win32Exception (Marshal.GetLastWin32Error (), "Could not assign the child process to its containment Job Object.");
                }

                return;
            }

            CaptureDescendants ();
            _trackingTask = Task.Run (TrackDescendantsAsync);
        }

        internal void TerminateRemaining ()
        {
            if (_job is not null)
            {
                _job.Dispose ();

                return;
            }

            CaptureDescendants ();
            KillTrackedDescendants ();
        }

        internal async Task CompleteAsync ()
        {
            if (_completed)
            {
                return;
            }

            _trackingStop.Cancel ();

            if (_trackingTask is not null)
            {
                await WaitBoundedNoThrowAsync (_trackingTask, CleanupTimeout).ConfigureAwait (false);
            }

            if (_job is not null)
            {
                // Closing a kill-on-close job also removes successful-command descendants that
                // detached their standard handles and would otherwise outlive the operation.
                _job.Dispose ();
            }
            else
            {
                CaptureDescendants ();
                KillTrackedDescendants ();
            }

            _completed = true;
        }

        public void Dispose ()
        {
            _trackingStop.Cancel ();
            _job?.Dispose ();

            if (!_completed && _job is null)
            {
                CaptureDescendants ();
                KillTrackedDescendants ();
                KillIdentity (_rootPid, _rootStarted);
            }

            _trackingStop.Dispose ();
        }

        private async Task TrackDescendantsAsync ()
        {
            Stopwatch elapsed = Stopwatch.StartNew ();

            try
            {
                while (!_trackingStop.IsCancellationRequested)
                {
                    CaptureDescendants ();
                    TimeSpan delay = elapsed.Elapsed < TimeSpan.FromSeconds (2)
                                         ? TimeSpan.FromMilliseconds (50)
                                         : TimeSpan.FromSeconds (1);
                    await Task.Delay (delay, _trackingStop.Token).ConfigureAwait (false);
                }
            }
            catch (OperationCanceledException) when (_trackingStop.IsCancellationRequested)
            {
            }
        }

        private void CaptureDescendants ()
        {
            if (_rootPid <= 0 || OperatingSystem.IsWindows ())
            {
                return;
            }

            IReadOnlyDictionary<int, int> parents = SnapshotParentPids ();
            HashSet<int> ancestors = [];

            if (IsIdentityAlive (_rootPid, _rootStarted))
            {
                ancestors.Add (_rootPid);
            }

            ancestors.UnionWith (LiveTrackedPids ());

            bool added;

            do
            {
                added = false;

                foreach ((int pid, int parentPid) in parents)
                {
                    if (pid == Environment.ProcessId || ancestors.Contains (pid) || !ancestors.Contains (parentPid))
                    {
                        continue;
                    }

                    ancestors.Add (pid);
                    TrackIdentity (pid);
                    added = true;
                }
            }
            while (added);
        }

        private void TrackIdentity (int pid)
        {
            try
            {
                using Process descendant = Process.GetProcessById (pid);
                DateTime started = descendant.StartTime.ToUniversalTime ();

                lock (_gate)
                {
                    _trackedDescendants.TryAdd (pid, started);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
            {
            }
        }

        private void KillTrackedDescendants ()
        {
            KeyValuePair<int, DateTime> [] snapshot;

            lock (_gate)
            {
                snapshot = [.. _trackedDescendants];
            }

            foreach ((int pid, DateTime expectedStart) in snapshot.Reverse ())
            {
                KillIdentity (pid, expectedStart);
            }
        }

        private IReadOnlyList<int> LiveTrackedPids ()
        {
            KeyValuePair<int, DateTime> [] snapshot;

            lock (_gate)
            {
                snapshot = [.. _trackedDescendants];
            }

            List<int> live = [];
            List<int> stale = [];

            foreach ((int pid, DateTime expectedStart) in snapshot)
            {
                if (IsIdentityAlive (pid, expectedStart))
                {
                    live.Add (pid);
                }
                else
                {
                    stale.Add (pid);
                }
            }

            if (stale.Count > 0)
            {
                lock (_gate)
                {
                    foreach (int pid in stale)
                    {
                        _trackedDescendants.Remove (pid);
                    }
                }
            }

            return live;
        }

        private static bool IsIdentityAlive (int pid, DateTime expectedStart)
        {
            try
            {
                using Process process = Process.GetProcessById (pid);

                return process.StartTime.ToUniversalTime () == expectedStart && !process.HasExited;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
            {
                return false;
            }
        }

        private static void KillIdentity (int pid, DateTime expectedStart)
        {
            if (pid <= 0)
            {
                return;
            }

            try
            {
                using Process descendant = Process.GetProcessById (pid);

                if (descendant.StartTime.ToUniversalTime () == expectedStart && !descendant.HasExited)
                {
                    descendant.Kill (entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
            {
            }
        }

        private static IReadOnlyDictionary<int, int> SnapshotParentPids ()
        {
            if (OperatingSystem.IsLinux ())
            {
                return SnapshotLinuxParentPids ();
            }

            if (OperatingSystem.IsMacOS ())
            {
                return SnapshotMacParentPids ();
            }

            return new Dictionary<int, int> ();
        }

        private static IReadOnlyDictionary<int, int> SnapshotLinuxParentPids ()
        {
            Dictionary<int, int> parents = [];

            try
            {
                foreach (string directory in Directory.EnumerateDirectories ("/proc"))
                {
                    if (!int.TryParse (Path.GetFileName (directory), out int pid))
                    {
                        continue;
                    }

                    string stat = File.ReadAllText (Path.Combine (directory, "stat"));
                    int commandEnd = stat.LastIndexOf (')');

                    if (commandEnd < 0)
                    {
                        continue;
                    }

                    string [] fields = stat [(commandEnd + 2)..].Split (' ', StringSplitOptions.RemoveEmptyEntries);

                    if (fields.Length > 1 && int.TryParse (fields [1], out int parentPid))
                    {
                        parents [pid] = parentPid;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
            }

            return parents;
        }

        private static IReadOnlyDictionary<int, int> SnapshotMacParentPids ()
        {
            Dictionary<int, int> parents = [];

            try
            {
                int requiredBytes = ProcListPids (ProcAllPids, 0, null, 0);

                if (requiredBytes <= 0)
                {
                    return parents;
                }

                int [] pids = new int [requiredBytes / sizeof (int) + 64];
                int returnedBytes = ProcListPids (ProcAllPids, 0, pids, pids.Length * sizeof (int));
                byte [] info = new byte [256];

                for (int i = 0; i < returnedBytes / sizeof (int); i++)
                {
                    int pid = pids [i];

                    if (pid <= 0 || ProcPidInfo (pid, ProcPidTbsdInfo, 0, info, info.Length) < 20)
                    {
                        continue;
                    }

                    parents [pid] = BitConverter.ToInt32 (info, 16);
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
            }

            return parents;
        }

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
        private static extern bool AssignProcessToJobObject (SafeFileHandle job, IntPtr process);

        [DllImport ("/usr/lib/libproc.dylib", EntryPoint = "proc_listpids")]
        private static extern int ProcListPids (uint type, uint typeInfo, [Out] int []? buffer, int bufferSize);

        [DllImport ("/usr/lib/libproc.dylib", EntryPoint = "proc_pidinfo")]
        private static extern int ProcPidInfo (int pid, int flavor, ulong arg, [Out] byte [] buffer, int bufferSize);

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
}
