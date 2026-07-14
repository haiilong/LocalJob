using Microsoft.Extensions.Options;

namespace LocalJob.Sample;

/// <summary>
/// Fixed-rate example: flushes this instance's (simulated) in-memory metrics buffer every 500 ms.
/// Per-instance state is exactly the case where every replica must run its own copy of the job.
/// If a flush is still in flight when the next tick arrives, the tick is dropped.
/// </summary>
public sealed partial class MetricsFlushJob(IOptions<LocalJobOptions> options, ILogger<MetricsFlushJob> logger)
    : LocalFixedRateJob(options, logger)
{
    protected override TimeSpan GetJobInterval() => TimeSpan.FromMilliseconds(500);

    // Job-level configuration lives on the class and runs last, after appsettings:
    // a runaway flush is cut off after 5 seconds.
    protected override void ConfigureJobOptions(LocalJobOptions o)
    {
        o.ExecutionTimeout = TimeSpan.FromSeconds(5);
        o.CancelWhenDisabled = true;
    }

    protected override async Task ExecuteJobAsync(CancellationToken cancellationToken)
    {
        LogMetricsFlushJob(Logger, DateTimeOffset.Now);
        await Task.Delay(50, cancellationToken); // simulated flush work
    }

    [LoggerMessage(LogLevel.Information, "[metrics-flush] flushing local buffer at {Time:HH:mm:ss.fff}")]
    static partial void LogMetricsFlushJob(ILogger logger, DateTimeOffset time);
}
