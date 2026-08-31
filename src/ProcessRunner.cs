using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WingetTuiSharp;

/// <summary>
/// Runs a redirected child process with a finite lifetime, bounded output retention, and a
/// kernel-owned process-tree boundary. A gated copy of this executable establishes the boundary
/// before it is allowed to launch the requested target.
/// </summary>
internal static class ProcessRunner
{
    internal const int MaxCapturedCharactersPerStream = 1024 * 1024;
    internal const string TruncationMarker = "\n...[output truncated]...\n";
    internal static readonly int MaxCombinedCapturedCharacters = 2 * (MaxCapturedCharactersPerStream + TruncationMarker.Length);

    private const string WrapperFlag = "--internal-contained-process-wrapper";
    private const int SigKill = 9;
    private const int NoSuchProcess = 3;
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

        WrapperControlFiles controls = WrapperControlFiles.Create ();
        WrapperLaunch wrapper = ResolveWrapperLaunch ();
        ProcessStartInfo startInfo = new ()
        {
            FileName = wrapper.Executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = outputEncoding,
            StandardErrorEncoding = outputEncoding
        };

        foreach (string prefixArgument in wrapper.PrefixArguments)
        {
            startInfo.ArgumentList.Add (prefixArgument);
        }

        startInfo.ArgumentList.Add (WrapperFlag);
        startInfo.ArgumentList.Add (controls.Ready);
        startInfo.ArgumentList.Add (controls.Gate);
        startInfo.ArgumentList.Add (controls.Status);
        startInfo.ArgumentList.Add (Environment.ProcessId.ToString (CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add (executable);

        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add (arg);
        }

        using Process process = new () { StartInfo = startInfo };
        using ProcessContainment containment = ProcessContainment.Create ();
        Task<string>? stdoutTask = null;
        Task<string>? stderrTask = null;
        Task? allOutputTask = null;

        try
        {
            process.Start ();
            containment.Attach (process);
            stdoutTask = DrainBoundedAsync (process.StandardOutput);
            stderrTask = DrainBoundedAsync (process.StandardError);
            allOutputTask = Task.WhenAll (stdoutTask, stderrTask);

            using CancellationTokenSource deadline = new (timeout);
            using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource (
                cancellationToken,
                deadline.Token);

            try
            {
                int sessionId = await WaitForIntegerFileAsync (controls.Ready, lifetime.Token).ConfigureAwait (false);
                containment.MarkWrapperReady (process, sessionId);
                CreateGate (controls.Gate);

                int targetExitCode = await WaitForIntegerFileAsync (controls.Status, lifetime.Token).ConfigureAwait (false);

                // The wrapper deliberately remains alive after publishing the target's result.
                // Kill the still-owned job/group before the wrapper PID/PGID can be recycled.
                containment.TerminateRemaining (process);
                await FinishDrainAsync (process, allOutputTask).ConfigureAwait (false);

                string stdout = await stdoutTask.ConfigureAwait (false);
                string stderr = await stderrTask.ConfigureAwait (false);

                return (targetExitCode, string.Concat (stdout, stderr));
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                containment.TerminateRemaining (process);
                await FinishDrainAsync (process, allOutputTask).ConfigureAwait (false);

                cancellationToken.ThrowIfCancellationRequested ();
                string command = args.Count > 0 ? $" {args [0]}" : string.Empty;
                throw new TimeoutException ($"Process '{executable}{command}' exceeded its {timeout.TotalSeconds:0.###}-second deadline and was terminated.");
            }
        }
        catch
        {
            containment.TerminateRemaining (process);

            if (allOutputTask is not null)
            {
                await FinishDrainAsync (process, allOutputTask).ConfigureAwait (false);
            }

            throw;
        }
        finally
        {
            controls.Delete ();
        }
    }

    /// <summary>
    /// Handles the private launcher mode before normal argument processing. Arguments are passed
    /// end-to-end with <see cref="ProcessStartInfo.ArgumentList"/>; no shell or command-line
    /// quoting is involved. The target inherits stdin unchanged. On POSIX the wrapper calls
    /// setsid before announcing readiness; on Windows the parent assigns the gated wrapper to a
    /// kill-on-close Job Object before creating the gate file.
    /// </summary>
    internal static bool TryRunContainedWrapper (string [] args)
    {
        if (args.Length == 0 || args [0] != WrapperFlag)
        {
            return false;
        }

        RunContainedWrapper (args);

        return true;
    }

    private static void RunContainedWrapper (string [] args)
    {
        if (args.Length < 6 || !int.TryParse (args [4], NumberStyles.None, CultureInfo.InvariantCulture, out int ownerPid))
        {
            Environment.ExitCode = 125;

            return;
        }

        string readyPath = args [1];
        string gatePath = args [2];
        string statusPath = args [3];
        string targetExecutable = args [5];
        WrapperControlFiles controls = new (readyPath, gatePath, statusPath);

        if (!OperatingSystem.IsWindows ())
        {
            int sessionId = SetSessionId ();

            if (sessionId <= 1 || sessionId != Environment.ProcessId)
            {
                WriteIntegerFile (readyPath, -1);
                WaitForOwnerTermination (ownerPid, controls);

                return;
            }
        }

        WriteIntegerFile (readyPath, Environment.ProcessId);

        while (!File.Exists (gatePath))
        {
            ExitIfOwnerDied (ownerPid, controls);
            Thread.Sleep (5);
        }

        ProcessStartInfo targetStartInfo = new ()
        {
            FileName = targetExecutable,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        for (int i = 6; i < args.Length; i++)
        {
            targetStartInfo.ArgumentList.Add (args [i]);
        }

        int targetExitCode;

        try
        {
            using Process target = new () { StartInfo = targetStartInfo };
            target.Start ();

            while (!target.HasExited)
            {
                ExitIfOwnerDied (ownerPid, controls);
                Thread.Sleep (25);
            }

            targetExitCode = target.ExitCode;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            Console.Error.WriteLine ($"Could not start contained process '{targetExecutable}': {ex.Message}");
            targetExitCode = 127;
        }

        WriteIntegerFile (statusPath, targetExitCode);

        // The owner now has the target result and kills the kernel containment boundary. Staying
        // alive keeps the Job/PGID ownership stable until that kill; it also prevents PID reuse.
        WaitForOwnerTermination (ownerPid, controls);
    }

    private static void WaitForOwnerTermination (int ownerPid, WrapperControlFiles controls)
    {
        while (true)
        {
            ExitIfOwnerDied (ownerPid, controls);
            Thread.Sleep (100);
        }
    }

    private static void ExitIfOwnerDied (int ownerPid, WrapperControlFiles controls)
    {
        if (OperatingSystem.IsWindows () || GetParentProcessId () == ownerPid)
        {
            return;
        }

        controls.Delete ();
        KillProcess (-Environment.ProcessId, SigKill);
        Environment.FailFast ("Contained process owner exited unexpectedly.");
    }

    private static WrapperLaunch ResolveWrapperLaunch ()
    {
        string executableName = OperatingSystem.IsWindows () ? "winget-tui-sharp.exe" : "winget-tui-sharp";
        string localAppHost = Path.Combine (AppContext.BaseDirectory, executableName);

        if (File.Exists (localAppHost))
        {
            return new (localAppHost, []);
        }

        string? processPath = Environment.ProcessPath;

        if (!string.IsNullOrEmpty (processPath)
            && Path.GetFileNameWithoutExtension (processPath).Equals ("winget-tui-sharp", StringComparison.OrdinalIgnoreCase))
        {
            return new (processPath, []);
        }

        string assemblyPath = Path.Combine (AppContext.BaseDirectory, "winget-tui-sharp.dll");

        if (!string.IsNullOrEmpty (assemblyPath) && File.Exists (assemblyPath))
        {
            return new ("dotnet", [assemblyPath]);
        }

        throw new InvalidOperationException ("Could not locate the winget-tui-sharp launcher used for process containment.");
    }

    private static async Task<int> WaitForIntegerFileAsync (string path, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested ();

            try
            {
                if (File.Exists (path))
                {
                    string value = await File.ReadAllTextAsync (path, cancellationToken).ConfigureAwait (false);

                    if (int.TryParse (value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                    {
                        return parsed;
                    }
                }
            }
            catch (IOException)
            {
            }

            await Task.Delay (10, cancellationToken).ConfigureAwait (false);
        }
    }

    private static void WriteIntegerFile (string path, int value)
    {
        string temporary = path + ".tmp";
        File.WriteAllText (temporary, value.ToString (CultureInfo.InvariantCulture), new UTF8Encoding (false));
        File.Move (temporary, path, overwrite: true);
    }

    private static void CreateGate (string path)
    {
        using FileStream gate = new (path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1, FileOptions.WriteThrough);
        gate.WriteByte (1);
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

    private static async Task FinishDrainAsync (Process process, Task outputTask)
    {
        await WaitBoundedNoThrowAsync (process.WaitForExitAsync (), CleanupTimeout).ConfigureAwait (false);

        if (!await WaitBoundedNoThrowAsync (outputTask, CleanupTimeout).ConfigureAwait (false))
        {
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
            return task.IsCompleted;
        }
    }

    private sealed class ProcessContainment : IDisposable
    {
        // The target can only escape by deliberately creating a new POSIX session/process group,
        // or by requesting Windows job breakaway. winget is cooperative and all commands are
        // launched without a shell, so neither escape is part of the supported command contract.
        private const uint JobObjectLimitKillOnJobClose = 0x00002000;
        private const int JobObjectExtendedLimitInformationClass = 9;

        private readonly SafeFileHandle? _job;
        private int _processGroupId;
        private bool _terminated;

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
            if (_job is not null && !AssignProcessToJobObject (_job, process.Handle))
            {
                throw new Win32Exception (Marshal.GetLastWin32Error (), "Could not assign the gated wrapper to its containment Job Object.");
            }
        }

        internal void MarkWrapperReady (Process process, int sessionId)
        {
            if (sessionId != process.Id || sessionId <= 1)
            {
                throw new InvalidOperationException ("The contained-process wrapper reported an invalid session/process-group id.");
            }

            if (!OperatingSystem.IsWindows ())
            {
                int ownerGroup = GetProcessGroup ();

                if (sessionId == ownerGroup)
                {
                    throw new InvalidOperationException ("Refusing to target the owner's own POSIX process group.");
                }

                _processGroupId = sessionId;
            }
        }

        internal void TerminateRemaining (Process process)
        {
            if (_terminated)
            {
                return;
            }

            _terminated = true;

            if (_job is not null)
            {
                _job.Dispose ();
            }
            else if (_processGroupId > 1 && _processGroupId != GetProcessGroup ())
            {
                int result = KillProcess (-_processGroupId, SigKill);
                int error = Marshal.GetLastWin32Error ();

                if (result != 0 && error != NoSuchProcess)
                {
                    TryKillDirectProcess (process);
                }
            }
            else
            {
                TryKillDirectProcess (process);
            }
        }

        public void Dispose () => _job?.Dispose ();

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

    private static void TryKillDirectProcess (Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill (entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
        }
    }

    private sealed record WrapperLaunch (string Executable, IReadOnlyList<string> PrefixArguments);

    private sealed record WrapperControlFiles (string Ready, string Gate, string Status)
    {
        internal static WrapperControlFiles Create ()
        {
            string prefix = Path.Combine (Path.GetTempPath (), $"winget-tui-process-{Guid.NewGuid ():N}");

            return new (prefix + ".ready", prefix + ".gate", prefix + ".status");
        }

        internal void Delete ()
        {
            foreach (string path in new [] { Ready, Gate, Status, Ready + ".tmp", Status + ".tmp" })
            {
                try
                {
                    File.Delete (path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    [DllImport ("libc", EntryPoint = "setsid", SetLastError = true)]
    private static extern int SetSessionId ();

    [DllImport ("libc", EntryPoint = "getpgrp")]
    private static extern int GetProcessGroup ();

    [DllImport ("libc", EntryPoint = "getppid")]
    private static extern int GetParentProcessId ();

    [DllImport ("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int KillProcess (int pid, int signal);
}
