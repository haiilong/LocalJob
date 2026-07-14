using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalJob;

/// <summary>
/// Runs <see cref="LocalBackgroundJob.ExecuteJobAsync"/>, then waits for <see cref="GetJobInterval"/> before
/// running again. The interval is measured from the end of the previous run, so a slow iteration delays
/// the next one. Use this when "at least N seconds between runs" semantics are wanted.
/// </summary>
/// <remarks>
/// Runs on startup by default (<see cref="DefaultRunOnStartup"/> is true): run-then-wait naturally starts
/// with a run. Set <see cref="LocalJobOptions.RunOnStartup"/> to false to wait one interval first.
/// A configured <see cref="LocalJobOptions.Jitter"/> is drawn once at startup and offsets this replica's
/// schedule for the lifetime of the process.
/// </remarks>
public abstract class LocalIntervalJob : LocalBackgroundJob
{
    /// <summary>
    /// Implement to return the wait time between iterations. Re-read after every iteration, so a dynamic
    /// value takes effect on the next wait. Must be positive and at most 49.7 days.
    /// </summary>
    protected abstract TimeSpan GetJobInterval();

    /// <inheritdoc />
    protected override bool DefaultRunOnStartup => true;

    /// <inheritdoc />
    protected LocalIntervalJob(
        IOptions<LocalJobOptions> options,
        ILogger logger)
        : base(options, logger)
    {
    }

    /// <inheritdoc />
    protected LocalIntervalJob(
        IOptions<LocalJobOptions> options,
        ILogger logger,
        TimeProvider timeProvider)
        : base(options, logger, timeProvider)
    {
    }

    /// <inheritdoc />
    protected override async Task ExecuteJobLoopAsync(CancellationToken stoppingToken)
    {
        try
        {
            await DelayStartupJitterAsync(stoppingToken).ConfigureAwait(false);
            if (!ShouldRunOnStartup)
                await Task.Delay(ValidateJobInterval(GetJobInterval(), JobName), TimeProvider, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            if (IsEnabled)
            {
                Logger.LogDebug("Job {JobName} iteration starting", JobName);
                var startTs = TimeProvider.GetTimestamp();
                try
                {
                    await ExecuteIterationAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (OperationCanceledException)
                {
                    Logger.LogInformation("Job {JobName} iteration cancelled after the job was disabled.", JobName);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Job {JobName} execution failed.", JobName);
                }
                var elapsed = TimeProvider.GetElapsedTime(startTs);
                Logger.LogDebug("Job {JobName} iteration completed in {ElapsedMs}ms", JobName, elapsed.TotalMilliseconds);
            }

            try
            {
                await Task.Delay(ValidateJobInterval(GetJobInterval(), JobName), TimeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
