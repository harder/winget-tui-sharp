using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WingetTuiSharp;

/// <summary>Runs a child with finite lifetime, bounded output, and kernel process containment.</summary>
internal static class ProcessRunner
{
    internal const int MaxCapturedCharactersPerStream = 1024 * 1024;
    internal const string TruncationMarker = "\n...[output truncated]...\n";
    internal static readonly int MaxCombinedCapturedCharacters = 2 * (MaxCapturedCharactersPerStream + TruncationMarker.Length);

    private const string PosixExecFlag = "--internal-posix-contained-exec";
    private const int SigKill = 9;
    private const int NoSuchProcess = 3;
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds (5);

    internal static Task<(int Code, string Output)> RunAsync (
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
                   : RunPosixAsync (executable, args, outputEncoding, timeout, cancellationToken);
    }

    /// <summary>
    /// POSIX launch helper. It creates a new session and immediately replaces itself with the
    /// target using execvp, so the target keeps the same PID/PGID and no second runtime remains.
    /// stdin/stdout/stderr and exact argv boundaries survive exec unchanged.
    /// </summary>
    internal static bool TryExecPosixContainedTarget (string [] args)
    {
        if (OperatingSystem.IsWindows () || args.Length < 2 || args [0] != PosixExecFlag)
        {
            return false;
        }

        if (SetSessionId () != Environment.ProcessId)
        {
            Console.Error.WriteLine ($"Could not establish a contained process session: errno {Marshal.GetLastWin32Error ()}.");
            Environment.ExitCode = 125;

            return true;
        }

        string executable = args [1];
        string [] targetArgv = [executable, .. args.Skip (2)];
        IntPtr argv = IntPtr.Zero;
        IntPtr [] strings = new IntPtr [targetArgv.Length];

        try
        {
            argv = Marshal.AllocHGlobal ((targetArgv.Length + 1) * IntPtr.Size);

            for (int i = 0; i < targetArgv.Length; i++)
            {
                strings [i] = Marshal.StringToCoTaskMemUTF8 (targetArgv [i]);
                Marshal.WriteIntPtr (argv, i * IntPtr.Size, strings [i]);
            }

            Marshal.WriteIntPtr (argv, targetArgv.Length * IntPtr.Size, IntPtr.Zero);
            ExecVp (executable, argv);
            int error = Marshal.GetLastWin32Error ();
            Console.Error.WriteLine ($"Could not exec contained process '{executable}': errno {error}.");
            Environment.ExitCode = 127;

            return true;
        }
        finally
        {
            foreach (IntPtr value in strings)
            {
                if (value != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem (value);
                }
            }

            if (argv != IntPtr.Zero)
            {
                Marshal.FreeHGlobal (argv);
            }
        }
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

    private static async Task<(int Code, string Output)> RunPosixAsync (
        string executable,
        IReadOnlyList<string> args,
        Encoding outputEncoding,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        WrapperLaunch helper = ResolvePosixHelperLaunch ();
        ProcessStartInfo startInfo = CreateRedirectedStartInfo (helper.Executable, outputEncoding);

        foreach (string prefixArgument in helper.PrefixArguments)
        {
            startInfo.ArgumentList.Add (prefixArgument);
        }

        startInfo.ArgumentList.Add (PosixExecFlag);
        startInfo.ArgumentList.Add (executable);

        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add (arg);
        }

        using Process process = new () { StartInfo = startInfo };
        process.Start ();
        int processGroupId = process.Id;
        Task<string> stdoutTask = DrainBoundedAsync (process.StandardOutput);
        Task<string> stderrTask = DrainBoundedAsync (process.StandardError);
        Task allOutputTask = Task.WhenAll (stdoutTask, stderrTask);

        using CancellationTokenSource deadline = new (timeout);
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource (cancellationToken, deadline.Token);

        try
        {
            await process.WaitForExitAsync (lifetime.Token).ConfigureAwait (false);
            int exitCode = process.ExitCode;

            // The exec'd target remains the session leader. Kill its group on success too, before
            // waiting for pipe EOF, so an immediate parent exit cannot orphan a descendant.
            TerminatePosixGroup (process, processGroupId);
            await FinishDrainAsync (process, allOutputTask).ConfigureAwait (false);

            return (exitCode, string.Concat (await stdoutTask.ConfigureAwait (false), await stderrTask.ConfigureAwait (false)));
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            TerminatePosixGroup (process, processGroupId);
            await FinishDrainAsync (process, allOutputTask).ConfigureAwait (false);
            cancellationToken.ThrowIfCancellationRequested ();
            throw ProcessTimeout (executable, args, timeout);
        }
        catch
        {
            TerminatePosixGroup (process, processGroupId);
            await FinishDrainAsync (process, allOutputTask).ConfigureAwait (false);
            throw;
        }
    }

    private static async Task<(int Code, string Output)> RunWindowsAsync (
        string executable,
        IReadOnlyList<string> args,
        Encoding outputEncoding,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using WindowsChildProcess child = WindowsChildProcess.Start (executable, args, outputEncoding);
        Task<string> stdoutTask = DrainBoundedAsync (child.StandardOutput);
        Task<string> stderrTask = DrainBoundedAsync (child.StandardError);
        Task allOutputTask = Task.WhenAll (stdoutTask, stderrTask);
        using CancellationTokenSource deadline = new (timeout);
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource (cancellationToken, deadline.Token);

        try
        {
            await child.Process.WaitForExitAsync (lifetime.Token).ConfigureAwait (false);
            int exitCode = child.Process.ExitCode;
            child.TerminateJob ();
            await FinishDrainAsync (child.Process, allOutputTask).ConfigureAwait (false);

            return (exitCode, string.Concat (await stdoutTask.ConfigureAwait (false), await stderrTask.ConfigureAwait (false)));
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            child.TerminateJob ();
            await FinishDrainAsync (child.Process, allOutputTask).ConfigureAwait (false);
            cancellationToken.ThrowIfCancellationRequested ();
            throw ProcessTimeout (executable, args, timeout);
        }
        catch
        {
            child.TerminateJob ();
            await FinishDrainAsync (child.Process, allOutputTask).ConfigureAwait (false);
            throw;
        }
    }

    private static ProcessStartInfo CreateRedirectedStartInfo (string executable, Encoding encoding) => new ()
    {
        FileName = executable,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
        StandardOutputEncoding = encoding,
        StandardErrorEncoding = encoding
    };

    private static WrapperLaunch ResolvePosixHelperLaunch ()
    {
        string localAppHost = Path.Combine (AppContext.BaseDirectory, "winget-tui-sharp");

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

        if (File.Exists (assemblyPath))
        {
            return new ("dotnet", [assemblyPath]);
        }

        throw new InvalidOperationException ("Could not locate the POSIX process-containment helper.");
    }

    private static TimeoutException ProcessTimeout (string executable, IReadOnlyList<string> args, TimeSpan timeout)
    {
        string command = args.Count > 0 ? $" {args [0]}" : string.Empty;

        return new ($"Process '{executable}{command}' exceeded its {timeout.TotalSeconds:0.###}-second deadline and was terminated.");
    }

    private static void TerminatePosixGroup (Process process, int processGroupId)
    {
        // The helper PID is freshly allocated and cannot already name another process group. It
        // calls setsid before exec, and the Process object is retained until this method returns.
        // If the helper has not reached setsid, group kill returns ESRCH and direct tree-kill is
        // sufficient because the target cannot have started. Deliberate target setsid is the only
        // cooperative-contract escape.
        bool safeGroup = processGroupId > 1 && processGroupId != GetProcessGroup ();

        if (safeGroup)
        {
            int result = KillProcess (-processGroupId, SigKill);
            int error = Marshal.GetLastWin32Error ();

            if (result == 0 || error == NoSuchProcess)
            {
                TryKillDirectProcess (process);

                return;
            }
        }

        TryKillDirectProcess (process);

        // Close the only launch race: setsid may complete between the first group probe and the
        // direct kill. A second group kill is safe because this freshly allocated PID cannot have
        // named another process group before the helper, and a surviving descendant keeps the
        // group id reserved after the target exits.
        if (safeGroup)
        {
            KillProcess (-processGroupId, SigKill);
        }
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
        private bool _terminated;

        private WindowsChildProcess (Process process, StreamReader stdout, StreamReader stderr, WindowsJob job)
        {
            Process = process;
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
            IntPtr inheritedHandles = IntPtr.Zero;
            Process? process = null;
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
                WindowsChildProcess result = new (process, stdoutReader, stderrReader, job);
                process = null;
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

                if (attributeList != IntPtr.Zero)
                {
                    DeleteProcThreadAttributeList (attributeList);
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

    [DllImport ("libc", EntryPoint = "setsid", SetLastError = true)]
    private static extern int SetSessionId ();

    [DllImport ("libc", EntryPoint = "getpgrp")]
    private static extern int GetProcessGroup ();

    [DllImport ("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int KillProcess (int pid, int signal);

    [DllImport ("libc", EntryPoint = "execvp", SetLastError = true)]
    private static extern int ExecVp ([MarshalAs (UnmanagedType.LPUTF8Str)] string file, IntPtr argv);
}
