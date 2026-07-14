using Cronos;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using static LocalJob.Tests.TestHelpers;

namespace LocalJob.Tests;

public class CronJobTests
{
    private static IOptions<LocalJobOptions> Opts(Action<LocalJobOptions>? configure = null)
    {
        var o = new LocalJobOptions();
        configure?.Invoke(o);
        return Options.Create(o);
    }

    private static readonly DateTimeOffset Midnight = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Daily_cron_fires_on_schedule_under_fake_time()
    {
        var fake = new FakeTimeProvider(Midnight);
        var expr = CronExpression.Parse("0 3 * * *"); // daily at 03:00 UTC
        var job = new CountingCronJob(Opts(), NullLogger<CountingCronJob>.Instance,
            expr, "cron-daily", fake);

        using var cts = new CancellationTokenSource();
        await job.StartAsync(cts.Token);
        await SettleAsync();

        fake.Advance(TimeSpan.FromHours(2));
        await SettleAsync();
        job.RunCount.Should().Be(0, "02:00 is before the daily 03:00 schedule");

        fake.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => job.RunCount == 1);

        fake.Advance(TimeSpan.FromHours(24));
        await WaitUntilAsync(() => job.RunCount == 2);

        await cts.CancelAsync();
        await job.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Far_future_occurrence_does_not_crash_the_job_loop()
    {
        var fake = new FakeTimeProvider(Midnight);
        // 1st of June 2030: ~150 days away, well past Task.Delay's ~49.7 day limit. The sleep must be
        // chunked; passing the whole delay to Task.Delay would throw ArgumentOutOfRangeException.
        var expr = CronExpression.Parse("0 0 1 6 *");
        var job = new CountingCronJob(Opts(), NullLogger<CountingCronJob>.Instance,
            expr, "cron-farfuture", fake);

        using var cts = new CancellationTokenSource();
        await job.StartAsync(cts.Token);
        await SettleAsync();

        job.ExecuteTask.Should().NotBeNull();
        job.ExecuteTask!.IsFaulted.Should().BeFalse("long sleeps must be chunked, not passed to Task.Delay whole");
        job.RunCount.Should().Be(0);

        await cts.CancelAsync();
        await job.StopAsync(CancellationToken.None);
    }

    // Misfire scenario shared by the three policy tests: the 00:01 run is held open on a gate while
    // virtual time jumps to 00:10:30, so occurrences 00:02..00:10 pass without firing. Releasing the
    // gate lets the loop observe the misfire and apply its policy.
    // The clock starts at 00:00:30, NOT on a minute boundary: an instant that coincides exactly with a
    // cron occurrence counts as already-due at startup and would add a spurious extra run.
    private static readonly DateTimeOffset OffBoundaryStart = Midnight + TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Skip_policy_drops_occurrences_missed_while_a_run_overran()
    {
        var fake = new FakeTimeProvider(OffBoundaryStart);
        var expr = CronExpression.Parse("* * * * *"); // every minute
        var job = new GatedCronJob(Opts(), NullLogger<GatedCronJob>.Instance,
            expr, "cron-skip", fake, CronMisfirePolicy.Skip);

        using var cts = new CancellationTokenSource();
        await job.StartAsync(cts.Token);
        await SettleAsync();

        fake.Advance(TimeSpan.FromMinutes(1));
        await WaitUntilAsync(() => job.RunCount == 1); // 00:01 run starts and blocks on the gate

        fake.Advance(TimeSpan.FromMinutes(9));         // now 00:10:30, nine occurrences missed
        job.Release(100);                              // this and all future runs finish instantly
        await SettleAsync();
        job.RunCount.Should().Be(1, "Skip must drop the occurrences that passed while the run was in flight");

        fake.Advance(TimeSpan.FromSeconds(30));        // next future occurrence: 00:11
        await WaitUntilAsync(() => job.RunCount == 2);

        await cts.CancelAsync();
        await job.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task FireOnce_policy_runs_a_single_catch_up_after_an_overrun()
    {
        var fake = new FakeTimeProvider(OffBoundaryStart);
        var expr = CronExpression.Parse("* * * * *"); // every minute
        var job = new GatedCronJob(Opts(), NullLogger<GatedCronJob>.Instance,
            expr, "cron-fireonce", fake, CronMisfirePolicy.FireOnce);

        using var cts = new CancellationTokenSource();
        await job.StartAsync(cts.Token);
        await SettleAsync();

        fake.Advance(TimeSpan.FromMinutes(1));
        await WaitUntilAsync(() => job.RunCount == 1); // 00:01 run starts and blocks on the gate

        fake.Advance(TimeSpan.FromMinutes(9));         // now 00:10:30, nine occurrences missed
        job.Release(100);
        await WaitUntilAsync(() => job.RunCount == 2); // exactly one catch-up run
        await SettleAsync();
        job.RunCount.Should().Be(2, "FireOnce must collapse all missed occurrences into a single catch-up run");

        await cts.CancelAsync();
        await job.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CatchUp_policy_replays_every_missed_occurrence()
    {
        var fake = new FakeTimeProvider(OffBoundaryStart);
        var expr = CronExpression.Parse("* * * * *"); // every minute
        var job = new GatedCronJob(Opts(), NullLogger<GatedCronJob>.Instance,
            expr, "cron-catchup", fake, CronMisfirePolicy.CatchUp);

        using var cts = new CancellationTokenSource();
        await job.StartAsync(cts.Token);
        await SettleAsync();

        fake.Advance(TimeSpan.FromMinutes(1));
        await WaitUntilAsync(() => job.RunCount == 1); // 00:01 run starts and blocks on the gate

        fake.Advance(TimeSpan.FromMinutes(9));         // now 00:10:30, nine occurrences missed
        job.Release(100);
        await WaitUntilAsync(() => job.RunCount == 10, timeoutMs: 10000); // 00:02..00:10 replayed back-to-back
        await SettleAsync();
        job.RunCount.Should().Be(10, "CatchUp must replay each missed occurrence exactly once");

        await cts.CancelAsync();
        await job.StopAsync(CancellationToken.None);
    }
}
