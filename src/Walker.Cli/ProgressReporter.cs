using System.Diagnostics;
using Walker.Core;

namespace Walker.Cli;

// Progress is deliberately separate from the final JSON stream. Heartbeats indicate liveness,
// not a percentage or assurance that a test process is making useful progress.
public sealed class ProgressReporter : IDisposable
{
    private readonly TextWriter writer;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly TimeSpan budget;
    private readonly Timer timer;
    private readonly object gate = new();
    private VerificationProgress state = new("starting", Detail: "Verification starting");
    private readonly Dictionary<long, string> active = [];
    private long sequence;
    private bool disposed;
    private bool outputClosed;

    public ProgressReporter(TextWriter writer, TimeSpan budget, TimeSpan? interval = null)
    {
        this.writer = writer;
        this.budget = budget;
        Write(false);
        timer = new(_ => Heartbeat(), null, interval ?? TimeSpan.FromSeconds(15), interval ?? TimeSpan.FromSeconds(15));
    }

    public void Update(VerificationProgress progress)
    {
        lock (gate)
        {
            if (disposed) return;
            state = progress;
            Write(false);
        }
    }

    public void Heartbeat()
    {
        lock (gate) if (!disposed) Write(true);
    }

    public long ProcessStarted(ProcessRequest request)
    {
        lock (gate)
        {
            var id = ++sequence;
            // Do not print arbitrary arguments, environment values or process output.
            var operation = request.FileName == "dotnet" && request.Arguments.Count > 0 ? request.Arguments[0] : Path.GetFileName(request.FileName);
            active[id] = operation;
            return id;
        }
    }

    public void ProcessFinished(long id) { lock (gate) active.Remove(id); }

    private void Write(bool heartbeat)
    {
        if (outputClosed) return;
        var elapsed = (int)clock.Elapsed.TotalSeconds;
        var remaining = Math.Max(0, (int)Math.Ceiling((budget - clock.Elapsed).TotalSeconds));
        var counts = state.Total > 0 ? $"; {state.Completed}/{state.Total} mutants completed" : "";
        var work = active.Count > 0 ? "; active: " + string.Join(", ", active.Values.Order(StringComparer.Ordinal)) : "";
        try
        {
            writer.WriteLine($"[walker {(heartbeat ? "heartbeat" : "progress")}] elapsed {elapsed}s; remaining {remaining}s; phase {state.Phase}{counts}{work}; {state.Detail}");
            writer.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { outputClosed = true; }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            timer.Dispose();
        }
    }
}

public sealed class ProgressProcessRunner(IProcessRunner inner, ProgressReporter progress) : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
    {
        var id = progress.ProcessStarted(request);
        try { return await inner.RunAsync(request, cancellationToken); }
        finally { progress.ProcessFinished(id); }
    }
}
