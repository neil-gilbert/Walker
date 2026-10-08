using Walker.Cli;
using Walker.Core;
using Xunit;

namespace Walker.Tests;

public sealed class ProgressReporterTests
{
    [Fact]
    public async Task HeartbeatsContinueWhileChildProcessProducesNoOutput()
    {
        using var output = new HeartbeatWriter();
        using var reporter = new ProgressReporter(output, TimeSpan.FromHours(2), TimeSpan.FromMilliseconds(20));
        reporter.Update(new("baseline", Total: 20, Detail: "Building and testing ordinary code"));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new ProgressProcessRunner(new WaitingRunner(started, release), reporter);
        var child = runner.RunAsync(new("dotnet", ["test", "private-project", "--secret", "private-value"], "root"), default);
        await started.Task;
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await output.TwoHeartbeats.Task.WaitAsync(limit.Token);
        release.SetResult();
        await child;
        reporter.Dispose();
        var text = output.ToString();
        Assert.True(text.Split("[walker heartbeat]").Length >= 3, text);
        Assert.Contains("phase baseline", text);
        Assert.Contains("active: test", text);
        Assert.Contains("remaining", text);
        Assert.DoesNotContain("private-value", text);
        var finished = output.ToString();
        reporter.Heartbeat();
        Assert.Equal(finished, output.ToString());
    }

    [Fact]
    public void ExplicitHeartbeatShowsLatestMutantCountsAndClearsFinishedProcesses()
    {
        using var output = new StringWriter();
        using var reporter = new ProgressReporter(output, TimeSpan.FromSeconds(60), TimeSpan.FromHours(1));
        var process = reporter.ProcessStarted(new("dotnet", ["build"], "root"));
        reporter.Update(new("execution", 3, 20, "Testing a boundary"));
        reporter.Heartbeat();
        Assert.Contains("3/20 mutants completed; active: build", output.ToString());
        reporter.ProcessFinished(process);
        reporter.Update(new("finished", 4, 20, "incomplete; cleanup finished"));
        Assert.DoesNotContain("active:", output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Last());
    }

    private sealed class WaitingRunner(TaskCompletionSource started, TaskCompletionSource release) : IProcessRunner
    {
        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            started.SetResult();
            await release.Task.WaitAsync(cancellationToken);
            return new(0, "", "", 1);
        }
    }
    private sealed class HeartbeatWriter : StringWriter
    {
        private int count;
        public TaskCompletionSource TwoHeartbeats { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            if (value?.StartsWith("[walker heartbeat]") == true && ++count == 2) TwoHeartbeats.SetResult();
        }
    }
}
