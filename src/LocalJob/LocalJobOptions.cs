namespace LocalJob;

/// <summary>
/// Configuration for <see cref="LocalBackgroundJob"/>-derived jobs.
/// Bound from configuration by the source-generated <c>services.AddLocalJobs(config)</c>;
/// a job class overrides values for itself in <see cref="LocalBackgroundJob.ConfigureJobOptions"/>,
/// which runs last.
/// </summary>
public class LocalJobOptions
{
    /// <summary>Default appsettings section name: <c>"LocalJob"</c>.</summary>
    public const string SectionName = "LocalJob";

    /// <summary>
    /// Hard kill switch, evaluated once at startup. When false the job never executes; the hosted service
    /// starts and immediately idles. Set <c>"Enabled": false</c> in the <c>LocalJob</c> config section to
    /// disable every job in the project, or per job in <see cref="LocalBackgroundJob.ConfigureJobOptions"/>.
    /// For live (runtime) toggling, e.g. from a feature-flag service, override
    /// <see cref="LocalBackgroundJob.IsJobEnabledAsync"/> instead. Default true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Whether the job runs one iteration immediately at startup, before its regular schedule kicks in.
    /// When null (the default) each job shape supplies its own default: <see cref="LocalIntervalJob"/> runs
    /// on startup (run-then-wait is its natural semantic), <see cref="LocalFixedRateJob"/> and
    /// <see cref="LocalCronJob"/> do not (they wait for the first tick / occurrence).
    /// Set explicitly to override the shape default — project-wide via the config section, or per job in
    /// <see cref="LocalBackgroundJob.ConfigureJobOptions"/>. A job class can also override
    /// <see cref="LocalBackgroundJob.DefaultRunOnStartup"/> to change its own code-level default while
    /// still letting configuration win.
    /// </summary>
    public bool? RunOnStartup { get; set; }

    /// <summary>
    /// Maximum random delay used to desynchronize replicas. Because every replica runs every job, N pods
    /// deployed together would otherwise execute the same schedule in lockstep and stampede any shared
    /// resource (database, downstream API). With a non-zero jitter each replica draws a uniformly random
    /// delay in <c>[0, Jitter)</c>: <see cref="LocalIntervalJob"/> and <see cref="LocalFixedRateJob"/> draw
    /// once at startup, permanently offsetting each replica's schedule; <see cref="LocalCronJob"/> draws a
    /// fresh delay before every occurrence, since a startup offset would not spread wall-clock fire times.
    /// Default zero (no jitter).
    /// </summary>
    public TimeSpan Jitter { get; set; }

    /// <summary>
    /// Upper bound on a single iteration. When set, the <see cref="CancellationToken"/> passed to a job
    /// iteration fires once the iteration has run this long; the timeout is logged at Warning and the
    /// schedule continues with the next iteration. A job that ignores its token is not forcibly aborted,
    /// only requested to stop. Default null (no limit).
    /// </summary>
    public TimeSpan? ExecutionTimeout { get; set; }

    /// <summary>
    /// When true, the <see cref="CancellationToken"/> passed to a job iteration also fires when
    /// <see cref="LocalBackgroundJob.IsJobEnabledAsync"/> flips to false while the iteration is in flight,
    /// provided the job honors its token. When false (the default) a started iteration only observes host
    /// shutdown (and <see cref="ExecutionTimeout"/>, if set) and otherwise runs to completion.
    /// </summary>
    public bool CancelWhenDisabled { get; set; }

    /// <summary>
    /// How often <see cref="LocalBackgroundJob.IsJobEnabledAsync"/> is re-evaluated. The result is cached in
    /// <see cref="LocalBackgroundJob.IsEnabled"/> and checked by the job loops before each iteration, so a
    /// high-frequency job does not hammer your feature-flag backend. Default 5 seconds.
    /// </summary>
    public TimeSpan EnabledPollingInterval { get; set; } = TimeSpan.FromSeconds(5);

    // IOptions<T>.Value is a singleton shared by every job; each job clones it before applying its own
    // ConfigureJobOptions so mutations stay private. Keep in sync with the properties above.
    internal LocalJobOptions Clone() => new()
    {
        Enabled = Enabled,
        RunOnStartup = RunOnStartup,
        Jitter = Jitter,
        ExecutionTimeout = ExecutionTimeout,
        CancelWhenDisabled = CancelWhenDisabled,
        EnabledPollingInterval = EnabledPollingInterval,
    };

    // Task.Delay, PeriodicTimer, and CancellationTokenSource.CancelAfter all reject delays above
    // uint.MaxValue - 1 milliseconds (~49.7 days).
    private const double MaxTimerMilliseconds = 4294967294;

    internal void Validate()
    {
        if (EnabledPollingInterval <= TimeSpan.Zero || EnabledPollingInterval.TotalMilliseconds > MaxTimerMilliseconds)
            throw new InvalidOperationException(
                $"{nameof(LocalJobOptions)}.{nameof(EnabledPollingInterval)} ({EnabledPollingInterval}) must be positive and at most 49.7 days.");
        if (Jitter < TimeSpan.Zero || Jitter.TotalMilliseconds > MaxTimerMilliseconds)
            throw new InvalidOperationException(
                $"{nameof(LocalJobOptions)}.{nameof(Jitter)} ({Jitter}) must be non-negative and at most 49.7 days.");
        if (ExecutionTimeout is { } timeout && (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > MaxTimerMilliseconds))
            throw new InvalidOperationException(
                $"{nameof(LocalJobOptions)}.{nameof(ExecutionTimeout)} ({timeout}) must be positive and at most 49.7 days, or null for no limit.");
    }
}
