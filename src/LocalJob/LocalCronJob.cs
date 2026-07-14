using Cronos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalJob;

/// <summary>
/// Runs on a cron schedule. The job sleeps until the next occurrence of <see cref="GetCronExpression"/>,
/// then runs once. Use this for "once a day at 03:00", "every 15 minutes past the hour", and similar
/// wall-clock schedules. Every replica fires at the same wall-clock instants; configure
/// <see cref="LocalJobOptions.Jitter"/> to spread them out.
/// </summary>
/// <remarks>
/// Cron expressions are parsed by <see href="https://github.com/HangfireIO/Cronos">Cronos</see>. By default
/// the schedule is interpreted in UTC; override <see cref="TimeZone"/> to use a different zone.
/// Occurrences that pass without firing (slow execution, process suspension, clock jumps) are handled
/// according to <see cref="MisfirePolicy"/>; the default skips them and resumes at the next future
/// occurrence. Does not run at startup by default (<see cref="LocalBackgroundJob.DefaultRunOnStartup"/> is
/// false); set <see cref="LocalJobOptions.RunOnStartup"/> to true to run once immediately and then follow
/// the schedule. A configured <see cref="LocalJobOptions.Jitter"/> is drawn fresh before every run
/// (including the startup run), because cron occurrences are pinned to the wall clock and a one-time
/// startup offset would not spread them. Keep the jitter well below the cron period; occurrences that pass
/// while a jitter delay is pending are treated as misfires.
/// </remarks>
public abstract class LocalCronJob : LocalBackgroundJob
{
    // Task.Delay rejects anything above uint.MaxValue - 1 milliseconds (~49.7 days), which a sparse cron
    // (yearly, specific dates) easily exceeds. Sleeping in bounded chunks and recomputing the remaining
    // time also keeps the wake-up accurate if the system clock is adjusted during a long sleep.
    private static readonly TimeSpan MaxSleepChunk = TimeSpan.FromDays(1);

    /// <summary>Implement to return the parsed cron expression. Use <see cref="CronExpression.Parse(string)"/>.</summary>
    protected abstract CronExpression GetCronExpression();

    /// <summary>Time zone used to evaluate the cron expression. Defaults to UTC.</summary>
    protected virtual TimeZoneInfo TimeZone => TimeZoneInfo.Utc;

    /// <summary>
    /// How missed occurrences are handled. Defaults to <see cref="CronMisfirePolicy.Skip"/>. Override with
    /// <see cref="CronMisfirePolicy.FireOnce"/> for infrequent jobs (hourly, daily) where running late is
    /// better than not running at all.
    /// </summary>
    protected virtual CronMisfirePolicy MisfirePolicy => CronMisfirePolicy.Skip;

    /// <inheritdoc />
    protected LocalCronJob(
        IOptions<LocalJobOptions> options,
        ILogger logger)
        : base(options, logger)
    {
    }

    /// <inheritdoc />
    protected LocalCronJob(
        IOptions<LocalJobOptions> options,
        ILogger logger,
        TimeProvider timeProvider)
        : base(options, logger, timeProvider)
    {
    }

    /// <inheritdoc />
    protected override async Task ExecuteJobLoopAsync(CancellationToken stoppingToken)
    {
        var expr = GetCronExpression()
            ?? throw new InvalidOperationException($"Job '{JobName}': GetCronExpression() returned null.");

        if (ShouldRunOnStartup)
        {
            try
            {
                await DelayJitterAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (stoppingToken.IsCancellationRequested) return;
            await RunOccurrenceAsync("startup", stoppingToken).ConfigureAwait(false);
        }

        // Track the pivot for the next-occurrence lookup so the loop strictly advances even if Cronos
        // returns a value at or before the pivot for second-precision expressions like "* * * * * *".
        // The pivot starts in the past so the first lookup returns the very next occurrence.
        // The pivot is an absolute instant (UTC); TimeZone is passed to Cronos below, which interprets
        // the cron fields in that zone (including DST transitions) and returns an absolute instant back.
        // Doing the arithmetic on absolute instants keeps it unambiguous across DST changes.
        var pivot = TimeProvider.GetUtcNow().AddTicks(-1);

        while (!stoppingToken.IsCancellationRequested)
        {
            var next = expr.GetNextOccurrence(pivot, TimeZone, inclusive: false);
            if (next is null)
            {
                Logger.LogWarning("Cron job {JobName} has no future occurrences. Stopping loop.", JobName);
                return;
            }

            // Defensive guard: Cronos should always return a value strictly greater than `pivot` when
            // inclusive=false, but guard against any edge case to avoid a busy loop.
            if (next.Value <= pivot)
            {
                pivot = pivot.AddTicks(1);
                continue;
            }

            pivot = next.Value;

            var now = TimeProvider.GetUtcNow();
            if (next.Value <= now)
            {
                // The occurrence already passed: a misfire. Happens when the previous execution (or a
                // pending jitter delay) overran the cron period, the process was suspended, or the clock
                // jumped forward.
                switch (MisfirePolicy)
                {
                    case CronMisfirePolicy.Skip:
                        Logger.LogWarning(
                            "Cron job {JobName} missed scheduled time {ScheduledTime:O}. Policy Skip: resuming at the next future occurrence.",
                            JobName, next.Value);
                        pivot = now;
                        continue;
                    case CronMisfirePolicy.FireOnce:
                        // Collapse everything missed into the single immediate run below.
                        Logger.LogWarning(
                            "Cron job {JobName} missed scheduled time {ScheduledTime:O}. Policy FireOnce: running one catch-up execution now.",
                            JobName, next.Value);
                        pivot = now;
                        break;
                    default: // CatchUp: fire for this occurrence now; the loop replays the rest one by one.
                        Logger.LogDebug(
                            "Cron job {JobName} missed scheduled time {ScheduledTime:O}. Policy CatchUp: replaying it now.",
                            JobName, next.Value);
                        break;
                }
            }
            else
            {
                try
                {
                    var delay = next.Value - now;
                    while (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay < MaxSleepChunk ? delay : MaxSleepChunk, TimeProvider, stoppingToken).ConfigureAwait(false);
                        delay = next.Value - TimeProvider.GetUtcNow();
                    }

                    // Per-occurrence jitter: every replica reaches this occurrence at the same wall-clock
                    // instant, so each draws a fresh random delay to spread the fleet's fire times.
                    await DelayJitterAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            if (!IsEnabled) continue;

            await RunOccurrenceAsync(next.Value.ToString("O"), stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task DelayJitterAsync(CancellationToken stoppingToken)
    {
        var jitter = NextJitterDelay();
        if (jitter <= TimeSpan.Zero) return;

        Logger.LogDebug("Cron job {JobName} jitter: delaying this run by {JitterMs}ms", JobName, jitter.TotalMilliseconds);
        await Task.Delay(jitter, TimeProvider, stoppingToken).ConfigureAwait(false);
    }

    private async Task RunOccurrenceAsync(string scheduledTime, CancellationToken stoppingToken)
    {
        Logger.LogDebug("Cron job {JobName} firing for scheduled time {ScheduledTime}", JobName, scheduledTime);
        var startTs = TimeProvider.GetTimestamp();
        try
        {
            await ExecuteIterationAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (OperationCanceledException)
        {
            Logger.LogInformation("Job {JobName} iteration cancelled after the job was disabled.", JobName);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Cron job {JobName} failed.", JobName);
        }
        var elapsed = TimeProvider.GetElapsedTime(startTs);
        Logger.LogDebug("Cron job {JobName} completed in {ElapsedMs}ms", JobName, elapsed.TotalMilliseconds);
    }
}
