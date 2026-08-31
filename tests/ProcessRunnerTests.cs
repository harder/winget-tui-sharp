using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace WingetTuiSharp.Tests;

public sealed class ProcessRunnerTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine (Path.GetTempPath (), $"winget-tui-process-tests-{Guid.NewGuid ():N}");
    private readonly List<int> _childProcessIds = [];

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
        _childProcessIds.Add (childPid);
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
        _childProcessIds.Add (childPid);

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
        _childProcessIds.Add (childPid);
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
        _childProcessIds.Add (childPid);
        await WaitForFileAsync (pidFile + ".parent-exited", TestContext.Current.CancellationToken);
        cancellation.Cancel ();

        await Assert.ThrowsAnyAsync<OperationCanceledException> (() => run);
        await AssertProcessStopsAsync (childPid);
    }

    [Fact]
    public async Task RunWithCodeAsync_SuccessKillsPipeHoldingDescendantAfterParentExitedImmediately ()
    {
        string pidFile = Path.Combine (_tempDirectory, "success-orphan-pipe-child.pid");
        FakeCommand command = CreateOrphaningScript ("success-orphan-pipe", pidFile, detachStandardHandles: false);

        (int code, _) = await CliBackend.RunWithCodeAsync (
            command.Arguments,
            command.Executable,
            TimeSpan.FromSeconds (10),
            TestContext.Current.CancellationToken);

        int childPid = await ReadPidAsync (pidFile, TestContext.Current.CancellationToken);
        _childProcessIds.Add (childPid);
        Assert.Equal (0, code);
        await AssertProcessStopsAsync (childPid);
    }

    [Fact]
    public async Task RunWithCodeAsync_SuccessKillsDetachedDescendantThatClosedPipes ()
    {
        string pidFile = Path.Combine (_tempDirectory, "orphan-detached-child.pid");
        FakeCommand command = CreateOrphaningScript ("orphan-detached", pidFile, detachStandardHandles: true);

        (int code, _) = await CliBackend.RunWithCodeAsync (
            command.Arguments,
            command.Executable,
            TimeSpan.FromSeconds (10),
            TestContext.Current.CancellationToken);

        int childPid = await ReadPidAsync (pidFile, TestContext.Current.CancellationToken);
        _childProcessIds.Add (childPid);
        Assert.Equal (0, code);
        await AssertProcessStopsAsync (childPid);
    }

    [Fact]
    public async Task RunWithCodeAsync_DrainsFloodedPipesButBoundsRetainedOutput ()
    {
        const int floodCharacters = ProcessRunner.MaxCapturedCharactersPerStream * 2;
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

    public void Dispose ()
    {
        foreach (int pid in _childProcessIds)
        {
            KillIfAlive (pid);
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
        string unixRedirection = detachStandardHandles ? " >/dev/null 2>&1" : string.Empty;
        string windowsRedirection = detachStandardHandles
                                        ? $" -RedirectStandardOutput '{escapedPowerShellPidFile}.out' -RedirectStandardError '{escapedPowerShellPidFile}.err'"
                                        : string.Empty;

        return CreateScript (
            name,
            unix: $"sleep 60{unixRedirection} & child=$!; printf '%s' \"$child\" > '{escapedUnixPidFile}'; exit 0",
            windows: $"$childProcess = Start-Process pwsh -ArgumentList '-NoProfile','-Command','Start-Sleep 60' -PassThru{windowsRedirection}; "
                     + $"[IO.File]::WriteAllText('{escapedPowerShellPidFile}', $childProcess.Id.ToString([CultureInfo]::InvariantCulture)); "
                     + "exit 0");
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
                string text = await File.ReadAllTextAsync (path, cancellationToken);

                if (int.TryParse (text, NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
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

    private static async Task AssertProcessStopsAsync (int pid)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds (10);

        while (DateTime.UtcNow < deadline)
        {
            if (!IsAlive (pid))
            {
                return;
            }

            await Task.Delay (25, TestContext.Current.CancellationToken);
        }

        Assert.False (IsAlive (pid), $"Descendant process {pid} survived cancellation/timeout.");
    }

    private static bool IsAlive (int pid)
    {
        try
        {
            using Process process = Process.GetProcessById (pid);

            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void KillIfAlive (int pid)
    {
        try
        {
            using Process process = Process.GetProcessById (pid);

            if (!process.HasExited)
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
}
