using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using static LocalJob.Tests.TestHelpers;

namespace LocalJob.Tests;

public class FixedRateJobTests
{
    private static IOptions<LocalJobOptions> Opts(Action<LocalJobOptions>? configure = null)
    {
        var o = new LocalJobOptions();
        configure?.Invoke(o);
        return Options.Create(o);
    }

    [Fact]
    public async Task Waits_one_full_period_before_the_first_run_by_default()
    {
        var fake = new FakeTimeProvider();
        var job = new CountingFixedRateJob(Opts(), NullLogger<CountingFixedRateJob>.Instance,
            interval: TimeSpan.FromSeconds(30), workDuration: TimeSpan.Zero, "fixedrate-default", fake);

        using var cts = new CancellationTokenSource();
        await job.StartAsync(cts.Token);

        await SettleAsync();
        job.RunCount.Should().Be(0, "fixed-rate jobs do not run on startup by default");

        fake.Advance(TimeSpan.FromSeconds(30));
        await WaitUntilAsync(() => job.RunCount == 1);

        fake.Advance(TimeSpan.FromSeconds(30));
        await WaitUntilAsync(() => job.RunCount == 2);

        await cts.CancelAsync();
        await job.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Slow_iteration_drops_overlapping_ticks()
    {
        // Real time on purpose: the work duration and the tick grid interact through actual scheduling.
        var job = new CountingFixedRateJob(Opts(), NullLogger<CountingFixedRateJob>.Instance,
            interval: TimeSpan.FromMilliseconds(100),
            workDuration: TimeSpan.FromMilliseconds(500),
            "fixedrate-slow");

        using var cts = new CancellationTokenSource();
        await job.StartAsync(cts.Token);

        // Window: ~2s. Tick every 100ms = ~20 ticks. Work = 500ms. So expected runs ~= 4.
        await Task.Delay(2200, cts.Token);
        await cts.CancelAsync();
        await job.StopAsync(CancellationToken.None);

        job.RunCount.Should().BeInRange(2, 6, "overlapping ticks must be dropped while previous run is in flight");
    }
}
