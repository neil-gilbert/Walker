using System.Diagnostics;
using Walker.Core;
namespace Walker.Execution;
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(request.FileName) { WorkingDirectory = request.WorkingDirectory,
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in request.Arguments) info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        using var drainCancellation = new CancellationTokenSource();
        var timer = Stopwatch.StartNew();
        process.Start();
        // Drain continuously to avoid full-pipe deadlocks. A daemon (e.g. a compiler server)
        // can inherit these handles, so EOF must never be required indefinitely after exit.
        var output = DrainAsync(process.StandardOutput, request.OutputLimit, drainCancellation.Token);
        var error = DrainAsync(process.StandardError, request.OutputLimit, drainCancellation.Token);
        try { await process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* Process exited during cancellation. */ }
            await process.WaitForExitAsync(CancellationToken.None);
            drainCancellation.Cancel();
            await Task.WhenAll(output, error);
            throw;
        }
        // Usually both streams have already reached EOF. Allow buffered output to finish,
        // then stop reading inherited handles that outlive the command.
        drainCancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
        var stdout = await output; var stderr = await error;
        cancellationToken.ThrowIfCancellationRequested();
        return new(process.ExitCode, stdout.Text, stderr.Text, timer.ElapsedMilliseconds, stdout.Truncated || stderr.Truncated);
    }
    private static async Task<(string Text, bool Truncated)> DrainAsync(StreamReader reader, int limit, CancellationToken token)
    {
        var buffer = new char[4096];
        var tail = new System.Text.StringBuilder();
        var truncated = false;
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
            {
                tail.Append(buffer, 0, read);
                if (tail.Length > limit) { truncated = true; tail.Remove(0, tail.Length - limit); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { truncated = true; }
        return (tail.ToString(), truncated);
    }
}
