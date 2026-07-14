using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using static LocalJob.Tests.TestHelpers;

namespace LocalJob.Tests;

public class IntervalJobTests
{
    private static IOptions<LocalJobOptions> Opts(Action<LocalJobOptions>? configure = null)
    {
        var o = new LocalJobOptions();
        configure?.Invoke(o);
        return Options.Create(o);
    }

    [Fact]
    public async Task Runs_on_startup_by_default_then_waits_interval()
    {
        var fake = new FakeTimeProvider();
        var job = new CountingIntervalJob(Opts(), NullLogger<CountingIntervalJob>.Instance,
            TimeSpan.FromMinutes(1), "interval-startup", fake);

        using var cts = new CancellationTokenSource();
        await job.StartAsync(cts.Token);

        await WaitUntilAsync(() => job.RunCount == 1);

        // No virtual time has passed since the startup run, so no second run.
        await SettleAsync();
        job.RunCount.Should().Be(1);

        fake.Advance(TimeSpan.FromMinutes(1));
        await WaitUntilAsync(() => job.RunCount == 2);

        fake.Advance(TimeSpan.FromMinutes(1));
        await WaitUntilAsync(() => job.RunCount == 3);

        await cts.CancelAsync();
        await job.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Exception_in_iteration_does_not_kill_the_loop()
    {
        var fake = new FakeTimeProvider();
        var job = new ThrowingIntervalJob(Opts(), NullLogger<ThrowingIntervalJob>.Instance,
            TimeSpan.FromSeconds(10), "interval-throws", fake);

        using var cts = new CancellationTokenSource();
        await job.StartAsync(cts.Token);

        await WaitUntilAsync(() => job.StartedCount == 1);

        fake.Advance(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => job.StartedCount == 2);

        fake.Advance(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => job.StartedCount == 3);

        job.ExecuteTask.Should().NotBeNull();
        job.ExecuteTask!.IsFaulted.Should().BeFalse("a failing iteration must be logged, not fatal");

        await cts.CancelAsync();
        await job.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Invalid_interval_faults_the_job_loop_with_a_clear_message()
    {
        var fake = new FakeTimeProvider();
        var job = new CountingIntervalJob(Opts(), NullLogger<CountingIntervalJob>.Instance,
            TimeSpan.Zero, "interval-invalid", fake);

        using var cts = new CancellationTokenSource();
        // RunOnStartup (interval default) runs one iteration, then the first Task.Delay validation throws.
        // The loop can fault synchronously inside StartAsync (BackgroundService returns a completed
        // ExecuteTask) or asynchronously; accept either surfacing path.
        try { await job.StartAsync(cts.Token); } catch (InvalidOperationException) { /* asserted below */ }

        await WaitUntilAsync(() => job.ExecuteTask is { IsCompleted: true });

        job.ExecuteTask!.IsFaulted.Should().BeTrue();
        job.ExecuteTask.Exception!.GetBaseException().Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Contain("interval-invalid").And.Contain("GetJobInterval");

        await cts.CancelAsync();
    }
}
