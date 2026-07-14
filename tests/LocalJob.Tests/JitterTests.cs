using Cronos;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using static LocalJob.Tests.TestHelpers;

namespace LocalJob.Tests;

// Jitter delays are drawn from Random.Shared, so these tests assert bounds rather than exact instants:
// a run must have happened once the full jitter window has elapsed, and never later than window + schedule.
public class JitterTests
{
    private static IOptions<LocalJobOptions> Opts(TimeSpan jitter, bool? runOnStartup = null) =>
        Options.Create(new LocalJobOptions { Jitter = jitter, RunOnStartup = runOnStartup });

    private static readonly DateTimeOffset Midnight = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Interval_startup_run_lands_within_the_jitter_window()
    {
        var fake = new FakeTimeProvider();
        var job = new CountingIntervalJob(Opts(jitter: TimeSpan.FromSeconds(10)),
            NullLogger<CountingIntervalJob>.Instance,
            TimeSpan.FromMinutes(10), "jitter-interval", fake);

        using var cts = new CancellationTokenSource();
        await job.StartAsync(cts.Token);
        await SettleAsync();

        // The startup run is held back by a random delay in [0, 10s); after advancing the full window
        // it must have fired exactly once (the next run needs another 10 virtual minutes).
        job.RunCount.Should().BeInRange(0, 1);
        fake.Advance(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => job.RunCount == 1);

        await SettleAsync();
        job.RunCount.Should().Be(1);

        await cts.CancelAsync();
        await job.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Zero_jitter_runs_the_startup_iteration_immediately()
    {
        var fake = new FakeTimeProvider();
        var job = new CountingIntervalJob(Opts(jitter: TimeSpan.Zero),
            NullLogger<CountingIntervalJob>.Instance,
            TimeSpan.FromMinutes(10), "jitter-zero", fake);

        using var cts = new CancellationTokenSource();
        await job.StartAsync(cts.Token);

        await WaitUntilAsync(() => job.RunCount == 1);

        await cts.CancelAsync();
        await job.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Fixed_rate_grid_is_offset_by_the_startup_jitter()
    {
        var fake = new FakeTimeProvider();
        var job = new CountingFixedRateJob(Opts(jitter: TimeSpan.FromSeconds(10)),
            NullLogger<CountingFixedRateJob>.Instance,
            interval: TimeSpan.FromMinutes(1), workDuration: TimeSpan.Zero, "jitter-fixedrate", fake);

        using var cts = new CancellationTokenSource();
        await job.StartAsync(cts.Token);
        await SettleAsync();

        // The tick grid only starts after the jitter delay, so first tick = jitter + one period.
        // Advance in two steps: the PeriodicTimer is created by the continuation of the jitter delay,
        // and it must exist before the period is advanced over.
        fake.Advance(TimeSpan.FromSeconds(10));
        await SettleAsync();
        job.RunCount.Should().Be(0, "the jitter delay offsets the grid; it does not fire a run by itself");

        fake.Advance(TimeSpan.FromMinutes(1));
        await WaitUntilAsync(() => job.RunCount == 1);

        await cts.CancelAsync();
        await job.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Cron_jitter_is_drawn_per_occurrence()
    {
        var fake = new FakeTimeProvider(Midnight);
        var expr = CronExpression.Parse("* * * * *"); // every minute
        var job = new CountingCronJob(Opts(jitter: TimeSpan.FromSeconds(5)),
            NullLogger<CountingCronJob>.Instance,
            expr, "jitter-cron", fake);

        using var cts = new CancellationTokenSource();
        await job.StartAsync(cts.Token);
        await SettleAsync();

        // Occurrence at 00:01 fires within [00:01, 00:01:05).
        fake.Advance(TimeSpan.FromMinutes(1));
        await SettleAsync();
        job.RunCount.Should().BeInRange(0, 1);
        fake.Advance(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => job.RunCount == 1);

        // Next occurrence at 00:02 draws a fresh delay and fires within its own window. Settle between
        // the two advances so the freshly drawn jitter timer exists before its window is advanced over.
        fake.Advance(TimeSpan.FromMinutes(1));
        await SettleAsync();
        fake.Advance(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => job.RunCount == 2);

        await cts.CancelAsync();
        await job.StopAsync(CancellationToken.None);
    }
}
