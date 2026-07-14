# Getting started

## Install

```sh
dotnet add package LocalJob
```

`net8.0` and `net10.0`. Pulls in `Cronos`; nothing else beyond the `Microsoft.Extensions.*` abstractions.

## Minimal worker

```csharp
using LocalJob;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddLocalJobs(builder.Configuration);

await builder.Build().RunAsync();
```

That is the whole setup. There are no connection strings because there is nothing to connect to. `AddLocalJobs` is emitted at compile time by the bundled source generator, and since there is no reflection in the registration path, the library is fully trimming- and NativeAOT-safe.

> **Heads-up: run `dotnet build` once before relying on the symbol.** The source generator only runs as part of a build, so a fresh checkout will show `CS1061: 'IServiceCollection' does not contain a definition for 'AddLocalJobs'` in the IDE until you build at least once. After the first build, the symbol is recognized and IntelliSense works normally.

## Three job shapes

### Interval (run, then wait)

```csharp
public sealed class HeartbeatJob(IOptions<LocalJobOptions> o, ILogger<HeartbeatJob> l)
    : LocalIntervalJob(o, l)
{
    protected override TimeSpan GetJobInterval() => TimeSpan.FromSeconds(1);
    protected override Task ExecuteJobAsync(CancellationToken ct) { /* ... */ return Task.CompletedTask; }
}
```

This is already a complete job. `JobName` defaults to the class name (`"HeartbeatJob"`); override it if you want a stable name that survives class renames.

Runs once immediately at startup (the interval shape's default), then waits `GetJobInterval()` after each run finishes. A slow iteration therefore delays the next one, which is what you want when the requirement is "at least N seconds between runs".

### Fixed-rate (fire on tick, drop overlapping ticks)

```csharp
public sealed class MetricsFlushJob(IOptions<LocalJobOptions> o, ILogger<MetricsFlushJob> l)
    : LocalFixedRateJob(o, l)
{
    protected override TimeSpan GetJobInterval() => TimeSpan.FromMilliseconds(500);
    // per-job settings live on the class, applied after appsettings:
    protected override void ConfigureJobOptions(LocalJobOptions o) => o.ExecutionTimeout = TimeSpan.FromSeconds(5);
    protected override Task ExecuteJobAsync(CancellationToken ct) { /* ... */ return Task.CompletedTask; }
}
```

If the previous run is still in flight when a tick arrives, that tick is dropped. No queueing, no overlap. The first tick fires one period after startup; set `RunOnStartup = true` to also fire immediately.

### Cron (wall-clock schedule)

```csharp
public sealed class TempCleanupJob(IOptions<LocalJobOptions> o, ILogger<TempCleanupJob> l)
    : LocalCronJob(o, l)
{
    private static readonly CronExpression Expr = CronExpression.Parse("0 3 * * *");
    protected override CronExpression GetCronExpression() => Expr;
    // optional: protected override TimeZoneInfo TimeZone => TimeZoneInfo.FindSystemTimeZoneById("Asia/Singapore");
    // or if you prefer local time: protected override TimeZoneInfo TimeZone => TimeZoneInfo.Local;
    protected override Task ExecuteJobAsync(CancellationToken ct) { /* ... */ return Task.CompletedTask; }
}
```

Remember: **every replica fires at the same wall-clock instants.** If the job touches a shared resource, configure a [`Jitter`](configuration.md#jitter) to spread the fleet. Or step back and ask whether the work is actually global, in which case it belongs in [SingletonJob](https://github.com/haiilong/SingletonJob) instead.

## Run multiple instances

```sh
cd samples
docker compose up --build --scale worker=3
```

All three workers print job ticks, visibly offset from each other by the sample's 2 s jitter. Kill any container and the others are unaffected, because nothing is shared.

For Windows, see `samples/run-3-instances.ps1`.
