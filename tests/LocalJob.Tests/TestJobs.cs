using Cronos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalJob.Tests;

internal sealed class CountingIntervalJob(
    IOptions<LocalJobOptions> options,
    ILogger<CountingIntervalJob> logger,
    TimeSpan interval,
    string jobName,
    TimeProvider? timeProvider = null)
    : LocalIntervalJob(options, logger, timeProvider ?? TimeProvider.System)
{
    public int RunCount;

    public override string JobName { get; } = jobName;
    protected override TimeSpan GetJobInterval() => interval;

    protected override Task ExecuteJobAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref RunCount);
        return Task.CompletedTask;
    }
}

internal sealed class ThrowingIntervalJob(
    IOptions<LocalJobOptions> options,
    ILogger<ThrowingIntervalJob> logger,
    TimeSpan interval,
    string jobName,
    TimeProvider? timeProvider = null)
    : LocalIntervalJob(options, logger, timeProvider ?? TimeProvider.System)
{
    public int StartedCount;

    public override string JobName { get; } = jobName;
    protected override TimeSpan GetJobInterval() => interval;

    protected override Task ExecuteJobAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref StartedCount);
        throw new InvalidOperationException("boom");
    }
}

internal sealed class ToggleableIntervalJob(
    IOptions<LocalJobOptions> options,
    ILogger<ToggleableIntervalJob> logger,
    TimeSpan interval,
    string jobName)
    : LocalIntervalJob(options, logger)
{
    public int RunCount;
    private volatile bool _enabled = true;

    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public override string JobName { get; } = jobName;
    protected override TimeSpan GetJobInterval() => interval;

    protected override ValueTask<bool> IsJobEnabledAsync(CancellationToken cancellationToken) => new(_enabled);

    protected override Task ExecuteJobAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref RunCount);
        return Task.CompletedTask;
    }
}

// Blocks inside ExecuteJobAsync until its token fires; used to observe CancelWhenDisabled and
// ExecutionTimeout behavior. The block is a real-time delay so a FakeTimeProvider-driven timeout can
// interrupt it via the token.
internal sealed class BlockingIntervalJob(
    IOptions<LocalJobOptions> options,
    ILogger<BlockingIntervalJob> logger,
    string jobName,
    TimeProvider? timeProvider = null)
    : LocalIntervalJob(options, logger, timeProvider ?? TimeProvider.System)
{
    public int StartedCount;
    public volatile bool IterationCancelled;
    private volatile bool _enabled = true;

    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public override string JobName { get; } = jobName;
    protected override TimeSpan GetJobInterval() => TimeSpan.FromMilliseconds(100);

    protected override ValueTask<bool> IsJobEnabledAsync(CancellationToken cancellationToken) => new(_enabled);

    protected override async Task ExecuteJobAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref StartedCount);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            IterationCancelled = true;
            throw;
        }
    }
}

internal sealed class CountingFixedRateJob(
    IOptions<LocalJobOptions> options,
    ILogger<CountingFixedRateJob> logger,
    TimeSpan interval,
    TimeSpan workDuration,
    string jobName,
    TimeProvider? timeProvider = null)
    : LocalFixedRateJob(options, logger, timeProvider ?? TimeProvider.System)
{
    public int RunCount;

    public override string JobName { get; } = jobName;
    protected override TimeSpan GetJobInterval() => interval;

    protected override async Task ExecuteJobAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref RunCount);
        if (workDuration > TimeSpan.Zero)
            await Task.Delay(workDuration, cancellationToken);
    }
}

internal sealed class CountingCronJob(
    IOptions<LocalJobOptions> options,
    ILogger<CountingCronJob> logger,
    CronExpression expr,
    string jobName,
    TimeProvider? timeProvider = null,
    CronMisfirePolicy misfirePolicy = CronMisfirePolicy.Skip)
    : LocalCronJob(options, logger, timeProvider ?? TimeProvider.System)
{
    public int RunCount;

    public override string JobName { get; } = jobName;
    protected override CronExpression GetCronExpression() => expr;
    protected override CronMisfirePolicy MisfirePolicy => misfirePolicy;

    protected override Task ExecuteJobAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref RunCount);
        return Task.CompletedTask;
    }
}

// A fixed-rate job whose code-level default is to run on startup, used to verify that
// DefaultRunOnStartup can be overridden per job class and that an explicit option still wins.
internal sealed class EagerFixedRateJob(
    IOptions<LocalJobOptions> options,
    ILogger<EagerFixedRateJob> logger,
    TimeSpan interval,
    string jobName,
    TimeProvider? timeProvider = null)
    : LocalFixedRateJob(options, logger, timeProvider ?? TimeProvider.System)
{
    public int RunCount;

    public override string JobName { get; } = jobName;
    protected override TimeSpan GetJobInterval() => interval;
    protected override bool DefaultRunOnStartup => true;

    protected override Task ExecuteJobAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref RunCount);
        return Task.CompletedTask;
    }
}

// A cron job whose iterations block on a gate until the test releases them. Holding a run open while
// virtual time jumps is the deterministic way to manufacture a misfire: it does not depend on how
// FakeTimeProvider schedules continuations during a large Advance() sweep.
internal sealed class GatedCronJob(
    IOptions<LocalJobOptions> options,
    ILogger<GatedCronJob> logger,
    CronExpression expr,
    string jobName,
    TimeProvider? timeProvider = null,
    CronMisfirePolicy misfirePolicy = CronMisfirePolicy.Skip)
    : LocalCronJob(options, logger, timeProvider ?? TimeProvider.System)
{
    private readonly SemaphoreSlim _gate = new(0);
    public int RunCount;

    public void Release(int count = 1) => _gate.Release(count);

    public override string JobName { get; } = jobName;
    protected override CronExpression GetCronExpression() => expr;
    protected override CronMisfirePolicy MisfirePolicy => misfirePolicy;

    protected override async Task ExecuteJobAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref RunCount);
        await _gate.WaitAsync(cancellationToken);
    }
}

// No JobName override: exercises the GetType().Name default.
internal sealed class DefaultNameJob(
    IOptions<LocalJobOptions> options,
    ILogger<DefaultNameJob> logger,
    TimeProvider? timeProvider = null)
    : LocalIntervalJob(options, logger, timeProvider ?? TimeProvider.System)
{
    public int RunCount;

    protected override TimeSpan GetJobInterval() => TimeSpan.FromHours(1);

    protected override Task ExecuteJobAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref RunCount);
        return Task.CompletedTask;
    }
}

// Pins RunOnStartup in ConfigureJobOptions; used to verify the class hook runs last (beats the
// configuration-provided value). Fixed-rate so the effect is observable without advancing time.
internal sealed class SelfStartingFixedRateJob(
    IOptions<LocalJobOptions> options,
    ILogger<SelfStartingFixedRateJob> logger,
    TimeSpan interval,
    string jobName,
    TimeProvider? timeProvider = null)
    : LocalFixedRateJob(options, logger, timeProvider ?? TimeProvider.System)
{
    public int RunCount;

    public override string JobName { get; } = jobName;
    protected override TimeSpan GetJobInterval() => interval;

    protected override void ConfigureJobOptions(LocalJobOptions o) => o.RunOnStartup = true;

    protected override Task ExecuteJobAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref RunCount);
        return Task.CompletedTask;
    }
}

// Disables itself in ConfigureJobOptions; used to verify options mutations stay private to the job.
internal sealed class SelfDisablingIntervalJob(
    IOptions<LocalJobOptions> options,
    ILogger<SelfDisablingIntervalJob> logger,
    string jobName,
    TimeProvider? timeProvider = null)
    : LocalIntervalJob(options, logger, timeProvider ?? TimeProvider.System)
{
    public int RunCount;

    public override string JobName { get; } = jobName;
    protected override TimeSpan GetJobInterval() => TimeSpan.FromHours(1);

    protected override void ConfigureJobOptions(LocalJobOptions o) => o.Enabled = false;

    protected override Task ExecuteJobAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref RunCount);
        return Task.CompletedTask;
    }
}

// Sets an invalid value in ConfigureJobOptions; StartAsync must reject it with the job's name.
internal sealed class BrokenConfigJob(
    IOptions<LocalJobOptions> options,
    ILogger<BrokenConfigJob> logger,
    string jobName)
    : LocalIntervalJob(options, logger)
{
    public override string JobName { get; } = jobName;
    protected override TimeSpan GetJobInterval() => TimeSpan.FromHours(1);

    protected override void ConfigureJobOptions(LocalJobOptions o) => o.ExecutionTimeout = TimeSpan.Zero;

    protected override Task ExecuteJobAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

// Two distinct classes deliberately sharing a JobName, for the duplicate-name startup guard tests.
internal sealed class DuplicateNameJobA(
    IOptions<LocalJobOptions> options,
    ILogger<DuplicateNameJobA> logger,
    string jobName)
    : LocalIntervalJob(options, logger)
{
    public override string JobName { get; } = jobName;
    protected override TimeSpan GetJobInterval() => TimeSpan.FromHours(1);
    protected override Task ExecuteJobAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class DuplicateNameJobB(
    IOptions<LocalJobOptions> options,
    ILogger<DuplicateNameJobB> logger,
    string jobName)
    : LocalIntervalJob(options, logger)
{
    public override string JobName { get; } = jobName;
    protected override TimeSpan GetJobInterval() => TimeSpan.FromHours(1);
    protected override Task ExecuteJobAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
