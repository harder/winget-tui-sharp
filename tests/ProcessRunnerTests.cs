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
