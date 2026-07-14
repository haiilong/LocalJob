using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using static LocalJob.Tests.TestHelpers;

namespace LocalJob.Tests;

public class ExecutionTimeoutTests
{
    private static IOptions<LocalJobOptions> Opts(TimeSpan? timeout) =>
        Options.Create(new LocalJobOptions { ExecutionTimeout = timeout });

    [Fact]
    public async Task Iteration_exceeding_the_timeout_is_cancelled_and_the_schedule_continues()
    {
        var fake = new FakeTimeProvider();
        // BlockingIntervalJob waits 30 real seconds on its token; the 5s (virtual) timeout must cut it short.
        var job = new BlockingIntervalJob(Opts(TimeSpan.FromSeconds(5)),
            NullLogger<BlockingIntervalJob>.Instance, "timeout-cancel", fake);

        using var cts = new CancellationTokenSource();
        await job.StartAsync(cts.Token);
        await WaitUntilAsync(() => job.StartedCount == 1);

        fake.Advance(TimeSpan.FromSeconds(6));
        await WaitUntilAsync(() => job.IterationCancelled);

        job.ExecuteTask!.IsFaulted.Should().BeFalse("a timed-out iteration is logged at Warning, not fatal");

        // The loop moves on: after the job's 100ms interval, the next iteration starts.
        fake.Advance(TimeSpan.FromMilliseconds(200));
        await WaitUntilAsync(() => job.StartedCount == 2);

        await cts.CancelAsync();
        await job.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task No_timeout_by_default_lets_an_iteration_run_indefinitely()
    {
        var fake = new FakeTimeProvider();
        var job = new BlockingIntervalJob(Opts(timeout: null),
            NullLogger<BlockingIntervalJob>.Instance, "timeout-none", fake);

        using var cts = new CancellationTokenSource();
        await job.StartAsync(cts.Token);
        await WaitUntilAsync(() => job.StartedCount == 1);

        fake.Advance(TimeSpan.FromMinutes(10));
        await SettleAsync();
        job.IterationCancelled.Should().BeFalse("without ExecutionTimeout nothing bounds an iteration");

        // Shutdown still cancels the iteration so the host is not held for 30 seconds.
        await cts.CancelAsync();
        await job.StopAsync(CancellationToken.None);
        job.IterationCancelled.Should().BeTrue();
    }
}
