using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalJob;

/// <summary>
/// Base class for local (per-replica) background jobs. Every instance of the process runs its own copy of
/// the job on its own schedule; there is no cross-instance coordination, no external backend, and no leader
/// election. Concrete jobs should derive from <see cref="LocalIntervalJob"/>, <see cref="LocalFixedRateJob"/>,
/// or <see cref="LocalCronJob"/>. For "exactly one replica runs the job" semantics use
/// <see href="https://github.com/haiilong/SingletonJob">SingletonJob</see> instead.
/// </summary>
public abstract class LocalBackgroundJob : BackgroundService
{
    /// <summary>Logger for derived classes to use.</summary>
    protected readonly ILogger Logger;

    private readonly IOptions<LocalJobOptions> _optionsSource;
    private LocalJobOptions _options = null!;

    /// <summary>
    /// Name for this job, used in log lines and the duplicate-name startup guard. Defaults to the concrete
    /// class name (<c>GetType().Name</c>), which is what most jobs want. Override for a stable name that
    /// survives class renames, or to disambiguate two job classes that share a simple name across
    /// namespaces.
    /// </summary>
    public virtual string JobName => GetType().Name;

    /// <summary>Implement to perform a single iteration of the job.</summary>
    protected abstract Task ExecuteJobAsync(CancellationToken cancellationToken);

    /// <summary>Implemented by job-shape classes (interval, fixed-rate, cron) to define the execution loop.</summary>
    protected abstract Task ExecuteJobLoopAsync(CancellationToken stoppingToken);

    /// <summary>
    /// Clock used for all waits, jitter delays, execution timeouts, and schedule evaluation. Defaults to
    /// <see cref="System.TimeProvider.System"/>; pass a <c>FakeTimeProvider</c> through the constructor to
    /// drive the job with virtual time in tests.
    /// </summary>
    protected TimeProvider TimeProvider { get; }

    /// <summary>The configured options for this job (resolved on <see cref="StartAsync"/>).</summary>
    protected LocalJobOptions Options => _options;

    private bool _isEnabled = true;

    /// <summary>
    /// True when the job is currently enabled, as last observed by the enablement loop. Refreshed once per
    /// <see cref="LocalJobOptions.EnabledPollingInterval"/> from <see cref="IsJobEnabledAsync"/>.
    /// </summary>
    protected bool IsEnabled => Volatile.Read(ref _isEnabled);

    /// <summary>
    /// Code-level default for <see cref="LocalJobOptions.RunOnStartup"/>, consulted when the option is null.
    /// <see cref="LocalIntervalJob"/> overrides this to true (run-then-wait is its natural semantic);
    /// <see cref="LocalFixedRateJob"/> and <see cref="LocalCronJob"/> keep false (wait for the first
    /// tick / occurrence). Override in a concrete job to change its own default in code; an explicit
    /// <see cref="LocalJobOptions.RunOnStartup"/> value — from configuration or from
    /// <see cref="ConfigureJobOptions"/> — always wins.
    /// </summary>
    protected virtual bool DefaultRunOnStartup => false;

    /// <summary>The resolved run-on-startup decision: <see cref="LocalJobOptions.RunOnStartup"/> if set, else <see cref="DefaultRunOnStartup"/>.</summary>
    protected bool ShouldRunOnStartup => _options.RunOnStartup ?? DefaultRunOnStartup;

    // One source per enabled term, created on (re-)enable and cancelled when the live flag flips to
    // disabled. Cancelled but never disposed: an in-flight iteration may still hold a linked source over
    // the token, and a CTS without timers needs no disposal.
    private CancellationTokenSource? _termCts;

    // Process-wide registry of job names, used to catch two different job classes sharing a JobName
    // (typically two classes with the same simple name in different namespaces, since the default name is
    // GetType().Name). With a shared name, log lines from the two jobs become indistinguishable. Keyed by
    // name and storing the concrete type so multiple instances of the SAME class (for example,
    // multi-instance simulations in tests) stay allowed.
    private static readonly ConcurrentDictionary<string, Type> RegisteredJobNames = new();

    /// <summary>Initializes the base job with options and a logger, using the system clock.</summary>
    protected LocalBackgroundJob(
        IOptions<LocalJobOptions> options,
        ILogger logger)
        : this(options, logger, TimeProvider.System)
    {
    }

    /// <summary>
    /// Initializes the base job with an explicit <see cref="System.TimeProvider"/>. All waits, jitter
    /// delays, execution timeouts, and schedule evaluations go through it, so tests can drive the job with
    /// a <c>FakeTimeProvider</c> instead of waiting in real time.
    /// </summary>
    protected LocalBackgroundJob(
        IOptions<LocalJobOptions> options,
        ILogger logger,
        TimeProvider timeProvider)
    {
        _optionsSource = options ?? throw new ArgumentNullException(nameof(options));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        TimeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>
    /// Code-level configuration for this job, applied <b>last</b> — after the option defaults and the
    /// <c>"LocalJob"</c> configuration section. Override to pin values the class insists on:
    /// <c>o.RunOnStartup = true</c>, <c>o.ExecutionTimeout = ...</c>, etc. For values that ops should be
    /// able to tune at deploy time, prefer the configuration section (project-wide) and leave them unset
    /// here. The instance passed in is this job's private copy; mutations never affect other jobs.
    /// Environment-dependent decisions are fine — inject <c>IHostEnvironment</c> or any other service into
    /// the job and consult it here. Runs once on <see cref="StartAsync"/>; the result is frozen.
    /// </summary>
    protected virtual void ConfigureJobOptions(LocalJobOptions options)
    {
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // IOptions<T>.Value is a singleton shared by every job, so clone before applying this job's
        // ConfigureJobOptions — mutations must stay private to this job. Resolving Value also triggers the
        // registered IValidateOptions over the base configuration. The clone is re-validated after the
        // class override, and frozen from here on: no live-reload subscription.
        _options = _optionsSource.Value.Clone();
        ConfigureJobOptions(_options);
        try
        {
            _options.Validate();
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"[Job: {JobName}] {ex.Message}", ex);
        }

        var owner = RegisteredJobNames.GetOrAdd(JobName, GetType());
        if (owner != GetType())
        {
            throw new InvalidOperationException(
                $"Duplicate job name: '{JobName}' is used by both {owner.FullName} and {GetType().FullName}. " +
                "Each job class must have a unique JobName, otherwise log lines from the two jobs become " +
                "indistinguishable. The default name is the class name, so this usually means two job " +
                "classes share a simple name across namespaces — override JobName on one of them.");
        }

        Logger.LogInformation("LocalJob started: {JobName}", JobName);
        return base.StartAsync(cancellationToken);
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        RegisteredJobNames.TryRemove(new KeyValuePair<string, Type>(JobName, GetType()));
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Controls whether this job may run, re-evaluated by the enablement loop once per
    /// <see cref="LocalJobOptions.EnabledPollingInterval"/>. The default returns true. Override to plug in a
    /// live toggle: inject your feature-flag service into the derived job and query it here. While the
    /// result is false this instance skips iterations; once it returns true again, iterations resume within
    /// one polling interval. An iteration already in flight when the flag flips is only cancelled when
    /// <see cref="LocalJobOptions.CancelWhenDisabled"/> is true.
    /// Exceptions are logged and the previous value is kept, so a flaky flag backend does not flap the job.
    /// Note: <see cref="LocalJobOptions.Enabled"/> is checked first and wins; a statically disabled job
    /// never calls this method.
    /// </summary>
    protected virtual ValueTask<bool> IsJobEnabledAsync(CancellationToken cancellationToken) => new(true);

    /// <inheritdoc />
    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            Logger.LogInformation(
                "Job {JobName} is disabled by configuration (LocalJobOptions.Enabled = false). It will not run.",
                JobName);
            return;
        }

        // Initial enabled term. Published before the job loop starts so any reader that observes
        // IsEnabled == true also observes a live term source.
        Volatile.Write(ref _termCts, new CancellationTokenSource());

        // Linked so the enablement loop also stops when the job loop exits for any non-shutdown reason
        // (invalid interval, an exception escaping the loop, a cron schedule with no future occurrences).
        // Without this the finally below would await the enablement loop until host shutdown, keeping the
        // failure invisible while no work runs.
        using var enablementCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var enablementTask = RunEnablementLoopAsync(enablementCts.Token);

        try
        {
            await ExecuteJobLoopAsync(stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            enablementCts.Cancel();
            try { await enablementTask.ConfigureAwait(false); } catch { /* swallow, already logged in loop */ }
        }
    }

    // Shared guard for the interval-returning shapes. Task.Delay and PeriodicTimer both reject values
    // outside (0, uint.MaxValue - 1 ms]; fail with a message that names the job instead of surfacing a
    // bare ArgumentOutOfRangeException from deep inside the loop.
    internal static TimeSpan ValidateJobInterval(TimeSpan interval, string jobName)
    {
        if (interval <= TimeSpan.Zero || interval.TotalMilliseconds > 4294967294)
        {
            throw new InvalidOperationException(
                $"Job '{jobName}': GetJobInterval() returned {interval}. " +
                "The interval must be positive and at most 49.7 days (uint.MaxValue - 1 milliseconds).");
        }
        return interval;
    }

    /// <summary>
    /// Draws a uniformly random delay in <c>[0, Options.Jitter)</c>, or <see cref="TimeSpan.Zero"/> when no
    /// jitter is configured.
    /// </summary>
    private protected TimeSpan NextJitterDelay() =>
        _options.Jitter > TimeSpan.Zero
            ? TimeSpan.FromTicks((long)(Random.Shared.NextDouble() * _options.Jitter.Ticks))
            : TimeSpan.Zero;

    /// <summary>
    /// Sleeps a randomly drawn startup jitter delay, used by the interval and fixed-rate shapes to
    /// desynchronize replicas for the lifetime of the process.
    /// </summary>
    private protected async Task DelayStartupJitterAsync(CancellationToken stoppingToken)
    {
        var jitter = NextJitterDelay();
        if (jitter <= TimeSpan.Zero) return;

        Logger.LogDebug(
            "Job {JobName} startup jitter: offsetting schedule by {JitterMs}ms",
            JobName, jitter.TotalMilliseconds);
        await Task.Delay(jitter, TimeProvider, stoppingToken).ConfigureAwait(false);
    }

    // Runs one iteration of user work. The token handed to ExecuteJobAsync fires on host shutdown, on
    // ExecutionTimeout (if set), and — with CancelWhenDisabled enabled — when the live enabled flag flips
    // to false mid-iteration. A timeout is swallowed here (logged at Warning, schedule continues); the
    // other cancellation paths propagate so the shape loop can log and react.
    private protected async Task ExecuteIterationAsync(CancellationToken stoppingToken)
    {
        // Snapshot: the enablement loop may rotate the term concurrently. A term that ended between the
        // caller's IsEnabled check and here means the linked token is already cancelled, which is correct.
        var term = _options.CancelWhenDisabled ? Volatile.Read(ref _termCts) : null;
        var timeout = _options.ExecutionTimeout;

        if (term is null && timeout is null)
        {
            await ExecuteJobAsync(stoppingToken).ConfigureAwait(false);
            return;
        }

        using var timeoutCts = timeout is { } limit ? new CancellationTokenSource(limit, TimeProvider) : null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            [stoppingToken, term?.Token ?? default, timeoutCts?.Token ?? default]);

        try
        {
            await ExecuteJobAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            timeoutCts is { IsCancellationRequested: true } && !stoppingToken.IsCancellationRequested)
        {
            Logger.LogWarning(
                "Job {JobName} iteration exceeded ExecutionTimeout ({TimeoutMs}ms) and was cancelled. " +
                "The schedule continues with the next iteration.",
                JobName, timeout!.Value.TotalMilliseconds);
        }
    }

    private async Task RunEnablementLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EvaluateEnabledAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await Task.Delay(_options.EnabledPollingInterval, TimeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async ValueTask EvaluateEnabledAsync(CancellationToken stoppingToken)
    {
        var previous = Volatile.Read(ref _isEnabled);
        bool enabled;
        try
        {
            enabled = await IsJobEnabledAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A flaky flag backend should not flap the job; keep the last known state.
            Logger.LogWarning(ex,
                "IsJobEnabledAsync threw for job {JobName}. Keeping previous state ({Enabled}).", JobName, previous);
            return;
        }

        if (enabled == previous) return;

        if (enabled)
        {
            // Publish the new term before the enabled flag so any reader that observes IsEnabled == true
            // also observes the term source for this enabled term.
            Volatile.Write(ref _termCts, new CancellationTokenSource());
            Volatile.Write(ref _isEnabled, true);
        }
        else
        {
            Volatile.Write(ref _isEnabled, false);
            Volatile.Read(ref _termCts)?.Cancel();
        }

        Logger.LogInformation("Job {JobName} is now {State}", JobName, enabled ? "ENABLED" : "DISABLED");
    }
}
