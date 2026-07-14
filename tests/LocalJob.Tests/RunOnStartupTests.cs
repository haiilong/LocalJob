using Cronos;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using static LocalJob.Tests.TestHelpers;

namespace LocalJob.Tests;

public class RunOnStartupTests
{
    private static IOptions<LocalJobOptions> Opts(bool? runOnStartup) =>
        Options.Create(new LocalJobOptions { RunOnStartup = runOnStartup });

    private static readonly DateTimeOffset Midnight = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Interval_job_with_option_false_waits_one_interval_first()
    {
        var fake = new FakeTimeProvider();
        var job = new CountingIntervalJob(Opts(runOnStartup: false), NullLogger<CountingIntervalJob>.Instance,
            TimeSpan.FromMinutes(1), "ros-interval-off", fake);

        using var cts = new CancellationTokenSource();
        await job.StartAsync(cts.Token);

        await SettleAsync();
        job.RunCount.Should().Be(0, "RunOnStartup=false overrides the interval shape's run-first default");

        fake.Advance(TimeSpan.FromMinutes(1));
        await WaitUntilAsync(() => job.RunCount == 1);

        await cts.CancelAsync();
        await job.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Fixed_rate_job_with_option_true_fires_immediately()
    {
        var fake = new FakeTimeProvider();
        var job = new CountingFixedRateJob(Opts(runOnStartup: true), NullLogger<CountingFixedRateJob>.Instance,
            interval: TimeSpan.FromMinutes(5), workDuration: TimeSpan.Zero, "ros-fixedrate-on", fake);

        using var cts = new CancellationTokenSource();
        await job.StartAsync(cts.Token);

        await WaitUntilAsync(() => job.RunCount == 1);

        fake.Advance(TimeSpan.FromMinutes(5));
        await WaitUntilAsync(() => job.RunCount == 2);

        await cts.CancelAsync();
        await job.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Cron_job_with_option_true_runs_once_at_startup_then_follows_the_schedule()
    {
        var fake = new FakeTimeProvider(Midnight);
        var expr = CronExpression.Parse("0 3 * * *"); // daily at 03:00 UTC
        var job = new CountingCronJob(Opts(runOnStartup: true), NullLogger<CountingCronJob>.Instance,
            expr, "ros-cron-on", fake);

        using var cts = new CancellationTokenSource();
        await job.StartAsync(cts.Token);

        await WaitUntilAsync(() => job.RunCount == 1, timeoutMs: 2000);

        fake.Advance(TimeSpan.FromHours(3) + TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => job.RunCount == 2);

        await cts.CancelAsync();
        await job.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Job_class_can_override_the_code_level_default()
    {
        var fake = new FakeTimeProvider();
        // EagerFixedRateJob overrides DefaultRunOnStartup to true; the option is left null.
        var job = new EagerFixedRateJob(Opts(runOnStartup: null), NullLogger<EagerFixedRateJob>.Instance,
            TimeSpan.FromMinutes(5), "ros-code-default", fake);

        using var cts = new CancellationTokenSource();
        await job.StartAsync(cts.Token);

        await WaitUntilAsync(() => job.RunCount == 1);

        await cts.CancelAsync();
        await job.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Explicit_option_beats_the_code_level_default()
    {
        var fake = new FakeTimeProvider();
        var job = new EagerFixedRateJob(Opts(runOnStartup: false), NullLogger<EagerFixedRateJob>.Instance,
            TimeSpan.FromMinutes(5), "ros-option-wins", fake);

        using var cts = new CancellationTokenSource();
        await job.StartAsync(cts.Token);

        await SettleAsync();
        job.RunCount.Should().Be(0, "an explicit RunOnStartup=false must override DefaultRunOnStartup=true");

        await cts.CancelAsync();
        await job.StopAsync(CancellationToken.None);
    }
}
