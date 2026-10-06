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
        // Skip per-invocation telemetry and banners; respect explicit user settings.
        foreach (var name in new[] { "DOTNET_CLI_TELEMETRY_OPTOUT", "DOTNET_NOLOGO", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE" })
            if (!info.Environment.ContainsKey(name)) info.Environment[name] = "1";
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
        // Keep only the last `limit` characters in a ring buffer: no per-chunk copying of retained output.
        var buffer = new char[4096];
        var ring = new char[limit];
        long written = 0;
        var truncated = false;
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
            {
                Append(ring, written, buffer.AsSpan(0, read));
                written += read;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { truncated = true; }
        if (written <= limit) return (new string(ring, 0, (int)written), truncated);
        var head = (int)(written % limit);
        return (string.Concat(ring.AsSpan(head), ring.AsSpan(0, head)), true);
    }
    private static void Append(char[] ring, long written, ReadOnlySpan<char> chunk)
    {
        var limit = ring.Length;
        var position = written + chunk.Length;
        if (chunk.Length > limit) chunk = chunk[^limit..];
        var start = (int)((position - chunk.Length) % limit);
        var first = Math.Min(chunk.Length, limit - start);
        chunk[..first].CopyTo(ring.AsSpan(start));
        chunk[first..].CopyTo(ring);
    }
}
