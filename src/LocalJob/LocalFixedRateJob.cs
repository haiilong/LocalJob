using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalJob;

/// <summary>
/// Runs at a fixed rate using <see cref="PeriodicTimer"/>. If the previous run is still in flight when a tick
/// arrives the tick is dropped (no overlapping execution, no queueing). Use this when "fire every N ms but
/// never overlap" semantics are wanted, for example a metrics flusher or a local buffer drainer.
/// </summary>
/// <remarks>
/// Waits one full period before the first run by default (<see cref="LocalBackgroundJob.DefaultRunOnStartup"/>
/// is false); set <see cref="LocalJobOptions.RunOnStartup"/> to true to also fire immediately at startup.
/// A configured <see cref="LocalJobOptions.Jitter"/> is drawn once at startup and permanently offsets this
/// replica's tick grid, so N replicas do not fire in lockstep.
/// </remarks>
public abstract class LocalFixedRateJob : LocalBackgroundJob
{
    private volatile bool _isJobRunning;
    private Task? _currentRun;

    /// <summary>
    /// Implement to return the period between ticks. Read once when the job starts (the
    /// <see cref="PeriodicTimer"/> is created with it), so later changes have no effect.
    /// Must be positive and at most 49.7 days.
    /// </summary>
    protected abstract TimeSpan GetJobInterval();

    /// <inheritdoc />
    protected LocalFixedRateJob(
        IOptions<LocalJobOptions> options,
        ILogger logger)
        : base(options, logger)
    {
    }

    /// <inheritdoc />
    protected LocalFixedRateJob(
        IOptions<LocalJobOptions> options,
        ILogger logger,
        TimeProvider timeProvider)
        : base(options, logger, timeProvider)
    {
    }

    /// <inheritdoc />
    protected override async Task ExecuteJobLoopAsync(CancellationToken stoppingToken)
    {
        var interval = ValidateJobInterval(GetJobInterval(), JobName);

        try
        {
            await DelayStartupJitterAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(interval, TimeProvider);

        try
        {
            if (ShouldRunOnStartup && IsEnabled)
            {
                _isJobRunning = true;
                _currentRun = ExecuteAndResetFlagAsync(stoppingToken);
            }

            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                if (!IsEnabled) continue;

                if (_isJobRunning)
                {
                    Logger.LogDebug("Job {JobName} tick dropped: previous run still in flight", JobName);
                    continue;
                }

                _isJobRunning = true;
                _currentRun = ExecuteAndResetFlagAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown, fall through to await any in-flight run
        }
        finally
        {
            // Wait for the most recent fire-and-forget run to finish so shutdown is graceful.
            if (_currentRun is { } run)
            {
                try { await run.ConfigureAwait(false); } catch { /* logged inside */ }
            }
        }
    }

    private async Task ExecuteAndResetFlagAsync(CancellationToken cancellationToken)
    {
        Logger.LogDebug("Job {JobName} iteration starting", JobName);
        var startTs = TimeProvider.GetTimestamp();
        try
        {
            await ExecuteIterationAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // graceful shutdown
        }
        catch (OperationCanceledException)
        {
            Logger.LogInformation("Job {JobName} iteration cancelled after the job was disabled.", JobName);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Job {JobName} failed during fixed-rate execution.", JobName);
        }
        finally
        {
            var elapsed = TimeProvider.GetElapsedTime(startTs);
            Logger.LogDebug("Job {JobName} iteration completed in {ElapsedMs}ms", JobName, elapsed.TotalMilliseconds);
            _isJobRunning = false;
        }
    }
}
