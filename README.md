# LocalJob

Lightweight in-memory recurring background jobs for .NET: interval, fixed-rate (drop-on-overlap), and cron schedules that run on **every instance** of your app. No Redis, no database, no coordination. The per-replica counterpart to [SingletonJob](https://github.com/haiilong/SingletonJob), with the same API shape.

[![NuGet](https://img.shields.io/nuget/v/LocalJob.svg)](https://www.nuget.org/packages/LocalJob/)
[![Build](https://github.com/haiilong/LocalJob/actions/workflows/ci.yml/badge.svg)](https://github.com/haiilong/LocalJob/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

## Why this exists

Some background work belongs to *the process*, not to *the deployment*: flushing a local metrics buffer, trimming this pod's temp directory, pinging a keep-alive, draining an in-memory queue. Deploy 5 replicas and all 5 must run it — coordinating them through Redis would be exactly wrong.

You can hand-roll a `BackgroundService` with a `while`/`Task.Delay` loop for each of these, but then every service reinvents the same details: overlap protection, cron parsing, misfire handling, graceful shutdown, error handling that doesn't kill the loop, per-job configuration, and testable time. `LocalJob` packages those once, behind the same job shapes and registration as `SingletonJob`, plus the features that only matter when N uncoordinated replicas run the same schedule:

- **Jitter** — N pods deployed together otherwise execute the same schedule in lockstep and stampede your database. One config value desynchronizes the fleet.
- **RunOnStartup** — explicit, per-shape-defaulted control over whether a job fires immediately at boot or waits for its first scheduled slot.
- **ExecutionTimeout** — a runaway iteration is cancelled instead of silently wedging the schedule.
- **Live enable/disable** — a feature-flag hook re-evaluated on a fixed cadence, with optional cancellation of in-flight work.

## Which library do I need?

|                                  | LocalJob                        | [SingletonJob](https://github.com/haiilong/SingletonJob) |
|----------------------------------|---------------------------------|-----------------------------------------|
| Who runs the job                 | **every** replica               | exactly **one** replica (leader election) |
| Backend                          | none (in-memory)                | Redis                                   |
| Typical work                     | per-instance state: local caches, buffers, temp files, keep-alives | global work: reports, syncs, outbox sweeps |
| Failover                         | not needed — nothing is shared  | automatic within seconds                |
| Anti-stampede jitter             | yes (built in)                  | not needed — only one runs              |
| Job shapes (interval / fixed-rate / cron) | yes / yes / yes        | yes / yes / yes                         |
| Registration                     | `AddLocalJobs()` (source-generated) | `AddSingletonJobs()` (source-generated) |
| AOT compatibility                | yes                             | yes                                     |

Sibling package: [RefreshAhead.MemoryCache](https://github.com/haiilong/RefreshAhead.MemoryCache) — when the per-instance work is specifically "refresh an in-memory cache and expose a snapshot", use that instead; it owns the cache-shaped API.

And versus [Hangfire](https://www.hangfire.io/): same trade as SingletonJob — no persistence, no retries, no dashboard, first-class sub-second schedules, drop-on-overlap semantics, tiny dependency graph, AOT-safe.

## Install

```sh
dotnet add package LocalJob
```

Targets `net8.0` and `net10.0` (If you use `net9.0` then it's the same as `net8.0`).

## Quickstart

```csharp
using LocalJob;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

// Source-generated, AOT-safe: registers every LocalBackgroundJob subclass at compile time.
builder.Services.AddLocalJobs(builder.Configuration);

await builder.Build().RunAsync();
```

> `AddLocalJobs` is emitted at compile time by the bundled Roslyn source generator. There is no reflection in the registration path, so the library is fully trimming- and NativeAOT-safe.
>
> **First build required.** Until the generator runs at least once, your IDE will red-squiggle the call with `CS1061: 'IServiceCollection' does not contain a definition for 'AddLocalJobs'`. Run `dotnet build` once and the symbol resolves. See [docs/troubleshooting.md](docs/troubleshooting.md) if it still doesn't.

`appsettings.json` (everything optional — LocalJob works with zero configuration):

```json
{
  "LocalJob": {
    "Jitter": "00:00:02"
  }
}
```

### Three job shapes

```csharp
// 1) Run, wait, run. "At least N seconds between runs." Runs on startup by default.
public sealed class HeartbeatJob(IOptions<LocalJobOptions> o, ILogger<HeartbeatJob> l)
    : LocalIntervalJob(o, l)
{
    protected override TimeSpan GetJobInterval() => TimeSpan.FromSeconds(1);
    protected override Task ExecuteJobAsync(CancellationToken ct) { /* ... */ return Task.CompletedTask; }
}

// 2) Fire on a fixed rate. Drop the tick if the previous run is still in flight.
public sealed class MetricsFlushJob(IOptions<LocalJobOptions> o, ILogger<MetricsFlushJob> l)
    : LocalFixedRateJob(o, l)
{
    protected override TimeSpan GetJobInterval() => TimeSpan.FromMilliseconds(500);
    // per-job settings live on the class, applied after appsettings:
    protected override void ConfigureJobOptions(LocalJobOptions o) => o.ExecutionTimeout = TimeSpan.FromSeconds(5);
    protected override Task ExecuteJobAsync(CancellationToken ct) { /* ... */ return Task.CompletedTask; }
}

// 3) Cron schedule.
public sealed class TempCleanupJob(IOptions<LocalJobOptions> o, ILogger<TempCleanupJob> l)
    : LocalCronJob(o, l)
{
    private static readonly CronExpression Expr = CronExpression.Parse("0 3 * * *");
    protected override CronExpression GetCronExpression() => Expr;
    // optional: protected override TimeZoneInfo TimeZone => TimeZoneInfo.FindSystemTimeZoneById("Asia/Singapore");
    // optional: protected override CronMisfirePolicy MisfirePolicy => CronMisfirePolicy.FireOnce;
    // optional: protected override void ConfigureJobOptions(LocalJobOptions o) => o.RunOnStartup = true;
    protected override Task ExecuteJobAsync(CancellationToken ct) { /* ... */ return Task.CompletedTask; }
}
```

No names, no per-job registration: `JobName` defaults to the class name (override it if you want a stable name that survives renames), and each class configures itself. Deploy N replicas. All N run the jobs — each on its own jitter-offset schedule.

## RunOnStartup

`RunOnStartup` is a nullable option. When unset, each shape supplies the default that matches its semantics:

| Shape              | Default | Rationale |
|--------------------|---------|-----------|
| `LocalIntervalJob` | `true`  | Run-then-wait naturally starts with a run. |
| `LocalFixedRateJob`| `false` | The tick grid starts one period after boot; an extra boot run would break the rate. |
| `LocalCronJob`     | `false` | Cron means "at these wall-clock times", not "and also whenever we deploy". |

Override it at any of three levels:

```csharp
// soft default for one job class (configuration can still override it):
protected override bool DefaultRunOnStartup => true;

// hard per-job setting, applied last (beats configuration):
protected override void ConfigureJobOptions(LocalJobOptions o) => o.RunOnStartup = true;
```

```json
// project-wide via configuration:
{ "LocalJob": { "RunOnStartup": false } }
```

## Jitter: don't stampede your database

Five replicas all restarting at deploy time, all running "every 30 s", hit your shared DB at the same instant, every 30 seconds, forever. Set a jitter:

```json
{ "LocalJob": { "Jitter": "00:00:05" } }
```

Each replica draws a uniformly random delay in `[0, Jitter)`:

- **interval / fixed-rate** — drawn once at startup, permanently offsetting that replica's schedule;
- **cron** — drawn fresh before every occurrence (all replicas share the same wall-clock fire times, so a one-time offset wouldn't help).

Keep the jitter well below the schedule period. Zero (the default) disables it.

## Configuration

| Option                  | Default    | Description                                                                 |
|-------------------------|------------|-----------------------------------------------------------------------------|
| `Enabled`               | `true`     | Static kill switch, evaluated once at startup. `false` = job never runs.    |
| `RunOnStartup`          | `null`     | Run once immediately at boot. `null` = shape default (see above).           |
| `Jitter`                | `00:00:00` | Max random delay to desynchronize replicas (see above).                     |
| `ExecutionTimeout`      | `null`     | Cancel an iteration running longer than this. `null` = unlimited.           |
| `CancelWhenDisabled`    | `false`    | Fire the iteration's token when the live flag turns off mid-run.            |
| `EnabledPollingInterval`| `00:00:05` | How often `IsJobEnabledAsync` is re-evaluated.                              |

No option is required: `AddLocalJobs()` with zero configuration is valid. Validation runs at host start; bad values throw `OptionsValidationException` before any job ticks. Per-job settings live on the job class itself — override `ConfigureJobOptions`, which runs after configuration and wins. See [docs/configuration.md](docs/configuration.md).

## Disabling jobs

Two mechanisms, layered — identical to SingletonJob:

```csharp
// Static (evaluated once at startup):
//   project level: appsettings.json "LocalJob": { "Enabled": false }
//   job level, on the class itself:
protected override void ConfigureJobOptions(LocalJobOptions o) => o.Enabled = false;

// Live (re-evaluated every EnabledPollingInterval): inject your feature-flag service
// into the job and override IsJobEnabledAsync:
public sealed class MetricsFlushJob(
    IOptions<LocalJobOptions> o, ILogger<MetricsFlushJob> l, IFeatureFlags flags)
    : LocalFixedRateJob(o, l)
{
    protected override async ValueTask<bool> IsJobEnabledAsync(CancellationToken ct)
        => await flags.IsEnabledAsync("jobs-enabled", ct)        // project-level flag
        && await flags.IsEnabledAsync($"job-{JobName}", ct);     // per-job flag
}
```

The flag is evaluated per replica, so a canary rollout can disable a job on one pod only. Set `CancelWhenDisabled = true` to also cancel an iteration already in flight when the flag flips. See [docs/configuration.md](docs/configuration.md#disabling-jobs).

## Logging levels

| Event                                          | Level       |
|------------------------------------------------|-------------|
| Service start, enabled/disabled transitions    | Information |
| Per-iteration start/end + duration, dropped ticks, jitter delays | Debug |
| `ExecutionTimeout` hit, cron misfires          | Warning     |
| Job exception                                  | Error       |

Per-iteration noise is at Debug on purpose. High-frequency jobs would otherwise flood Information logs.

> **Inside a job, log via the inherited `Logger` field — not the constructor parameter.** The base class already stores the logger in a `protected ILogger Logger`. Forwarding `logger` to `base(...)` *and* referencing it from your primary-constructor body creates a second backing field for the same value, which trips compiler warning [**CS9124**](docs/troubleshooting.md#cs9124-parameter-logger-is-captured-into-the-state-of-the-enclosing-type). Use `Logger.LogInformation(...)` (or a `[LoggerMessage]` static partial that takes `ILogger`, passed `Logger`) instead.

## Documentation

| | |
|---|---|
| [docs/getting-started.md](docs/getting-started.md) | Install + first three jobs |
| [docs/configuration.md](docs/configuration.md) | Every option, per-job overrides |
| [docs/architecture.md](docs/architecture.md) | The two loops, cancellation sources, jitter mechanics |
| [docs/aot.md](docs/aot.md) | NativeAOT + trimming, source generator details |
| [docs/deployment-kubernetes.md](docs/deployment-kubernetes.md) | Per-pod semantics, rolling deploys, SIGTERM |
| [docs/troubleshooting.md](docs/troubleshooting.md) | Common pitfalls and how to debug them |
| [CHANGELOG.md](CHANGELOG.md) | Release notes per version |

## Try it locally

See [`samples/`](samples/): a worker template with all three job types, a `docker-compose.yml` that spins up three workers (no other services needed), and a `run-3-instances.ps1` for Windows local dev.

```sh
cd samples
docker compose up --build --scale worker=3
```

All three workers tick — offset from each other by the sample's 2-second jitter.

## Roadmap

- Built-in `IHealthCheck` so readiness probes can detect a wedged job loop.
- Metrics via `System.Diagnostics.Metrics` (counters for ticks, dropped ticks, timeouts, durations).
- `ActivitySource` tracing per iteration for distributed tracing.

## License

MIT
