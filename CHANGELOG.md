# Changelog

All notable changes to this project are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] - 2026-07-14

First stable release. The public API is now considered stable and follows semver; breaking changes will only ship with a major version bump.

### Overview

`LocalJob` is an in-memory library for recurring background jobs where **every replica** of an N-node deployment executes the work independently: local caches, buffer flushes, temp-file cleanup, keep-alives. It is the per-replica counterpart to [SingletonJob](https://github.com/haiilong/SingletonJob), which elects exactly one leader via Redis, and it shares that library's API shape (same three job shapes, same source-generated registration). LocalJob itself needs no backend at all.

Targets `net8.0` and `net10.0`. `net9.0` consumers resolve to the `net8.0` TFM.

### Features

#### Three job shapes

- `LocalIntervalJob`: run, then wait `GetJobInterval()`, then run again. Wait time is measured from the end of the previous iteration. Use when you want "at least N seconds between runs" and slow iterations should naturally back off.
- `LocalFixedRateJob`: fire on a fixed-rate `PeriodicTimer` tick. If a previous run is still in flight when a tick arrives, the tick is dropped rather than queued, and the drop logs at `Debug`. On shutdown, the loop awaits the most recent in-flight run so termination remains graceful.
- `LocalCronJob`: wall-clock schedule via [Cronos](https://github.com/HangfireIO/Cronos). Override `TimeZone` to evaluate the expression in something other than UTC. Supports second-precision expressions, chunks sleeps longer than `Task.Delay`'s ~49.7-day limit, and is hardened against a non-advancing occurrence (no busy-spin).

All three derive from `LocalBackgroundJob`, which owns the enablement loop and exposes:

- `JobName` (virtual): defaults to the class name; used in log lines and the duplicate-name startup guard. Override it for a stable name that survives renames. This deliberately diverges from SingletonJob, where the name is part of a Redis lock key and therefore abstract.
- `ConfigureJobOptions(LocalJobOptions)` (virtual): per-job configuration on the class itself. Runs last, after the `"LocalJob"` configuration section, on a private clone of the options. There is no name-keyed registration API in the `PostConfigure...` style.
- `ExecuteJobAsync(CancellationToken)` (abstract): your work.
- `protected ILogger Logger`: log via this from derived classes to avoid CS9124 with primary-constructor jobs.
- `protected bool IsEnabled { get; }`: the cached live-toggle state, checked before each iteration.
- `protected TimeProvider TimeProvider`: every wait, timeout, and schedule evaluation goes through it, so tests can drive jobs across days of virtual time with `FakeTimeProvider` in milliseconds.

#### Run-on-startup control

`LocalJobOptions.RunOnStartup` (`bool?`, default `null`) decides whether a job fires one iteration immediately at boot. When unset, each shape supplies the default matching its semantics: interval `true` (run-then-wait starts with a run), fixed-rate `false` (the tick grid starts one period after boot), cron `false` (cron means wall-clock times, not deploy times). Override per project via configuration, per job in `ConfigureJobOptions` (pins the value and beats configuration), or as a soft code-level default via `protected override bool DefaultRunOnStartup` (configuration still wins over that one).

#### Anti-stampede jitter

`LocalJobOptions.Jitter` (default zero) draws a uniformly random delay in `[0, Jitter)` to desynchronize replicas. Without it, N pods deployed together execute the same schedule in lockstep and stampede shared resources. Interval and fixed-rate jobs draw once at startup, which permanently offsets the replica's schedule; cron jobs draw fresh before every occurrence (including a `RunOnStartup` run), because cron fire times are pinned to the wall clock.

#### Execution timeout

`LocalJobOptions.ExecutionTimeout` (default `null`, meaning unlimited) bounds a single iteration. On expiry the iteration's `CancellationToken` fires, the timeout logs at `Warning`, and the schedule continues with the next iteration. Cancellation is cooperative: a job that ignores its token is asked to stop, not aborted.

#### Job enable/disable

- `LocalJobOptions.Enabled` (default `true`): a static kill switch, evaluated once at startup. Set `"LocalJob": { "Enabled": false }` to disable every job in the project (handy per environment, e.g. staging), or `o.Enabled = false` in a job's `ConfigureJobOptions` for one job. A statically disabled job starts, logs one line, and idles.
- `protected virtual ValueTask<bool> IsJobEnabledAsync(CancellationToken)`: a live toggle, re-evaluated once per `EnabledPollingInterval` (default 5 s) by a dedicated enablement loop. Override it to bridge to a DI-injected feature-flag service; flips take effect within one polling interval without redeploy. The flag is evaluated per replica, so canary rollouts can disable a single pod. Exceptions from the override are logged and the previous state is kept.
- `LocalJobOptions.CancelWhenDisabled` (default `false`): when enabled, the token passed to `ExecuteJobAsync` also fires when the live flag turns off mid-iteration.

#### Cron misfire policy

`protected virtual CronMisfirePolicy MisfirePolicy` on `LocalCronJob` (default `Skip`). `Skip` drops missed occurrences and resumes at the next future one. `FireOnce` runs a single immediate catch-up execution covering everything missed, for hourly or daily jobs where running late beats not running. `CatchUp` replays every missed occurrence back-to-back for time-bucketed work. Misfires log at `Warning`, or `Debug` per replay under `CatchUp`.

#### Fail-fast guards

- Two different job classes resolving the same `JobName` throw at startup, since their log lines would be indistinguishable. With default names this means two classes sharing a simple name across namespaces.
- A zero, negative, or oversized `GetJobInterval()` fails with an `InvalidOperationException` naming the job, not a bare `ArgumentOutOfRangeException` from inside `Task.Delay`.
- Options are validated through `IValidateOptions<LocalJobOptions>` plus a startup hosted service, so bad configuration throws `OptionsValidationException` at host start, before any job ticks. Each job's `ConfigureJobOptions` result is re-validated when that job starts, with the message prefixed `[Job: name]`.
- When a job loop dies for a non-shutdown reason, its enablement loop is stopped immediately rather than lingering until host shutdown.

#### Source-generated DI registration

A bundled Roslyn source generator (`LocalJob.SourceGenerator`, shipped in the NuGet package's `analyzers/dotnet/cs` folder) scans your compilation for every non-abstract `LocalBackgroundJob` subclass and emits an `internal` `services.AddLocalJobs(IConfiguration?)` extension method into your assembly.

- There is no reflection in the registration path. Fully trimming- and NativeAOT-safe (`IsTrimmable=true`, `IsAotCompatible=true`).
- Configuration binding is manual (`section["Key"]` plus `TimeSpan.TryParse` / `bool.TryParse`), not `ConfigurationBinder.Bind`.
- The class is emitted as `internal` so each consuming assembly gets its own copy with no cross-project collision.
- Generic job classes are skipped and reported as warning `LJOB001` with guidance instead of emitting uncompilable code.

#### Logging

Predictable, low-noise structured logging:

| Event                                                    | Level         |
|----------------------------------------------------------|---------------|
| Service start, enabled/disabled transitions              | `Information` |
| Per-iteration start/end + duration, dropped ticks, jitter | `Debug`      |
| `ExecutionTimeout` hit, cron misfires                    | `Warning`     |
| Job exception                                            | `Error`       |

### Documentation

- [README.md](README.md): elevator pitch, LocalJob-vs-SingletonJob decision table, quickstart.
- [docs/getting-started.md](docs/getting-started.md): install plus the first three jobs.
- [docs/configuration.md](docs/configuration.md): every option and per-job configuration.
- [docs/architecture.md](docs/architecture.md): the two loops, cancellation sources, jitter mechanics.
- [docs/aot.md](docs/aot.md): NativeAOT, trimming, source-generator details.
- [docs/deployment-kubernetes.md](docs/deployment-kubernetes.md): per-pod semantics, rolling deploys, SIGTERM.
- [docs/troubleshooting.md](docs/troubleshooting.md): common pitfalls, CS9124 explained, log lines decoded.

### Samples

`samples/LocalJob.Sample` includes one job of each shape, a `docker-compose.yml` that spins up three workers (no other services), and `run-3-instances.ps1` for Windows local dev.

```sh
cd samples
docker compose up --build --scale worker=3
```

All three replicas tick, each offset by its own jitter draw.

### Dependencies

- [`Cronos`](https://www.nuget.org/packages/Cronos) `0.8.4`
- `Microsoft.Extensions.Hosting.Abstractions`, `Microsoft.Extensions.Options.ConfigurationExtensions`, `Microsoft.Extensions.Logging.Abstractions`, `Microsoft.Extensions.DependencyInjection.Abstractions`, all on the `8.0.x` line.

### Known limitations

These are documented constraints, not bugs. They may be revisited in future versions.

- No persistence, retries, or dashboard. If a fixed-rate tick is dropped (overlap) or a replica dies mid-execution, the work is not retried. Use Hangfire if you need durable work.
- No cross-replica coordination. Every replica runs every job; that is the design. If exactly one replica should run it, use [SingletonJob](https://github.com/haiilong/SingletonJob).
- No live config reload. Options are frozen per job in `StartAsync`. Redeploy to change them. (The `IsJobEnabledAsync` hook covers the "toggle at runtime" case.)
- Cooperative cancellation only. `ExecutionTimeout` and `CancelWhenDisabled` signal the iteration's token; work that ignores its token is not forcibly aborted.

### Roadmap (not in this release)

- Built-in `IHealthCheck` so readiness probes can detect a wedged job loop.
- Metrics via `System.Diagnostics.Metrics` (counters for ticks, dropped ticks, timeouts, durations).
- `ActivitySource` tracing per iteration.

[1.0.0]: https://github.com/haiilong/LocalJob/releases/tag/v1.0.0
