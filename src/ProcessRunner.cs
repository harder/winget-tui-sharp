using System.Buffers;
using System.Diagnostics;
using System.Text;

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
        process.Start ();

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
            await TerminateAndDrainAsync (process, allOutputTask).ConfigureAwait (false);

            // A caller cancellation remains cancellation; an internal deadline is a diagnosable
            // timeout. Check the caller first in case both tokens raced.
            cancellationToken.ThrowIfCancellationRequested ();
            string command = args.Count > 0 ? $" {args [0]}" : string.Empty;
            throw new TimeoutException ($"Process '{executable}{command}' exceeded its {timeout.TotalSeconds:0.###}-second deadline and was terminated.");
        }

        string stdout = await stdoutTask.ConfigureAwait (false);
        string stderr = await stderrTask.ConfigureAwait (false);

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

    private static async Task TerminateAndDrainAsync (Process process, Task outputTask)
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
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or NotSupportedException)
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
                                               or System.ComponentModel.Win32Exception
                                               or NotSupportedException)
            {
            }
        }

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
}
