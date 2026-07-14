using Microsoft.Extensions.Options;

namespace LocalJob.Sample;

public sealed partial class HeartbeatJob(IOptions<LocalJobOptions> options, ILogger<HeartbeatJob> logger)
    : LocalIntervalJob(options, logger)
{
    // JobName defaults to the class name ("HeartbeatJob"); no override needed.
    protected override TimeSpan GetJobInterval() => TimeSpan.FromSeconds(1);

    protected override Task ExecuteJobAsync(CancellationToken cancellationToken)
    {
        LogHeartbeatJob(Logger, DateTimeOffset.Now);
        return Task.CompletedTask;
    }

    [LoggerMessage(LogLevel.Information, "[heartbeat] tick at {Time:HH:mm:ss.fff}")]
    static partial void LogHeartbeatJob(ILogger logger, DateTimeOffset time);
}
