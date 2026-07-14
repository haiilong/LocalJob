using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using static LocalJob.Tests.TestHelpers;

namespace LocalJob.Tests;

public class ConfigureJobOptionsTests
{
    [Fact]
    public void JobName_defaults_to_the_class_name()
    {
        var job = new DefaultNameJob(Options.Create(new LocalJobOptions()), NullLogger<DefaultNameJob>.Instance);

        job.JobName.Should().Be(nameof(DefaultNameJob));
    }

    [Fact]
    public async Task Class_override_runs_last_and_beats_the_configured_value()
    {
        var fake = new FakeTimeProvider();
        // Configuration says "no startup run"; the class pins RunOnStartup = true and must win.
        var configured = Options.Create(new LocalJobOptions { RunOnStartup = false });
        var job = new SelfStartingFixedRateJob(configured, NullLogger<SelfStartingFixedRateJob>.Instance,
            TimeSpan.FromMinutes(5), "self-starting", fake);

        using var cts = new CancellationTokenSource();
        await job.StartAsync(cts.Token);

        await WaitUntilAsync(() => job.RunCount == 1);

        await cts.CancelAsync();
        await job.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Options_mutations_stay_private_to_the_job()
    {
        var fake = new FakeTimeProvider();
        // Both jobs resolve the SAME IOptions singleton. The self-disabling job must not leak its
        // Enabled = false into the shared instance or into the other job.
        var shared = Options.Create(new LocalJobOptions());
        var disabling = new SelfDisablingIntervalJob(shared, NullLogger<SelfDisablingIntervalJob>.Instance,
            "self-disabling", fake);
        var normal = new CountingIntervalJob(shared, NullLogger<CountingIntervalJob>.Instance,
            TimeSpan.FromHours(1), "unaffected-neighbor", fake);

        using var cts = new CancellationTokenSource();
        await disabling.StartAsync(cts.Token);
        await normal.StartAsync(cts.Token);

        await WaitUntilAsync(() => normal.RunCount == 1);
        await SettleAsync();
        disabling.RunCount.Should().Be(0, "the job disabled itself in ConfigureJobOptions");
        shared.Value.Enabled.Should().BeTrue("the shared options instance must never be mutated");

        await cts.CancelAsync();
        await disabling.StopAsync(CancellationToken.None);
        await normal.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Invalid_value_from_the_class_override_fails_at_StartAsync_with_the_job_name()
    {
        var job = new BrokenConfigJob(Options.Create(new LocalJobOptions()),
            NullLogger<BrokenConfigJob>.Instance, "broken-config");

        var act = async () => await job.StartAsync(CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*[Job: broken-config]*ExecutionTimeout*");
    }
}
