using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static LocalJob.Tests.TestHelpers;

namespace LocalJob.Tests;

// Real time on purpose: enable/disable flows through the enablement loop, the job loop, and (for
// CancelWhenDisabled) a token registration, and the interleaving is what these tests exercise.
public class EnablementTests
{
    private static IOptions<LocalJobOptions> Opts(
        bool enabled = true, bool cancelWhenDisabled = false) =>
        Options.Create(new LocalJobOptions
        {
            Enabled = enabled,
            CancelWhenDisabled = cancelWhenDisabled,
            EnabledPollingInterval = TimeSpan.FromMilliseconds(100),
        });

    [Fact]
    public async Task Statically_disabled_job_never_runs()
    {
        var job = new CountingIntervalJob(Opts(enabled: false),
            NullLogger<CountingIntervalJob>.Instance,
            TimeSpan.FromMilliseconds(50), "static-off");

        using var cts = new CancellationTokenSource();
        await job.StartAsync(cts.Token);

        await Task.Delay(500, cts.Token);
        await cts.CancelAsync();
        await job.StopAsync(CancellationToken.None);

        job.RunCount.Should().Be(0);
    }

    [Fact]
    public async Task Live_toggle_stops_runs_and_resumes()
    {
        var job = new ToggleableIntervalJob(Opts(),
            NullLogger<ToggleableIntervalJob>.Instance,
            TimeSpan.FromMilliseconds(50), "toggle");

        using var cts = new CancellationTokenSource();
        await job.StartAsync(cts.Token);

        await WaitUntilAsync(() => job.RunCount > 0);

        job.Enabled = false;
        // The disable is observed within one polling interval (100ms); after that runs must stop.
        await Task.Delay(400, cts.Token);
        var countAfterDisable = job.RunCount;
        await Task.Delay(500, cts.Token);
        job.RunCount.Should().Be(countAfterDisable, "a disabled job must not execute iterations");

        job.Enabled = true;
        await WaitUntilAsync(() => job.RunCount > countAfterDisable);

        await cts.CancelAsync();
        await job.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task In_flight_iteration_is_cancelled_when_disabled_with_CancelWhenDisabled()
    {
        var job = new BlockingIntervalJob(Opts(cancelWhenDisabled: true),
            NullLogger<BlockingIntervalJob>.Instance, "cancel-on");

        using var cts = new CancellationTokenSource();
        await job.StartAsync(cts.Token);
        await WaitUntilAsync(() => job.StartedCount > 0);

        // The live disable ends the enabled term, which must cancel the 30 second iteration in flight.
        job.Enabled = false;
        await WaitUntilAsync(() => job.IterationCancelled);

        await cts.CancelAsync();
        await job.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task In_flight_iteration_keeps_running_by_default()
    {
        var job = new BlockingIntervalJob(Opts(cancelWhenDisabled: false),
            NullLogger<BlockingIntervalJob>.Instance, "cancel-off");

        using var cts = new CancellationTokenSource();
        await job.StartAsync(cts.Token);
        await WaitUntilAsync(() => job.StartedCount > 0);

        job.Enabled = false;
        // Give the enablement loop several polling intervals to observe the disable.
        await Task.Delay(1000);
        job.IterationCancelled.Should().BeFalse("the default lets a started iteration run to completion");

        // Shutdown still cancels the iteration so the host is not held for 30 seconds.
        await cts.CancelAsync();
        await job.StopAsync(CancellationToken.None);
        job.IterationCancelled.Should().BeTrue();
    }
}
