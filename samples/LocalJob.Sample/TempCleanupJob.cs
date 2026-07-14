using Cronos;
using Microsoft.Extensions.Options;

namespace LocalJob.Sample;

/// <summary>
/// Cron example: cleans this instance's temp directory daily at 03:00 UTC. Every replica owns its own
/// filesystem, so every replica must run it — the classic per-instance chore.
/// </summary>
public sealed partial class TempCleanupJob(IOptions<LocalJobOptions> options, ILogger<TempCleanupJob> logger)
    : LocalCronJob(options, logger)
{
    private static readonly CronExpression Expr = CronExpression.Parse("0 3 * * *");

    protected override CronExpression GetCronExpression() => Expr;
    protected override TimeZoneInfo TimeZone => TimeZoneInfo.Utc;
    // For a daily chore, running late beats not running at all:
    protected override CronMisfirePolicy MisfirePolicy => CronMisfirePolicy.FireOnce;

    // Also run once at boot (instead of waiting for 03:00), so a fresh deployment starts clean.
    protected override void ConfigureJobOptions(LocalJobOptions o) => o.RunOnStartup = true;

    protected override Task ExecuteJobAsync(CancellationToken cancellationToken)
    {
        LogTempCleanupJob(Logger, DateTimeOffset.UtcNow);
        return Task.CompletedTask;
    }

    [LoggerMessage(LogLevel.Information, "[temp-cleanup] cleaning this instance's temp dir at {Time:O}")]
    static partial void LogTempCleanupJob(ILogger logger, DateTimeOffset time);
}
