# Architecture

`LocalJob` is deliberately the same machine as [SingletonJob](https://github.com/haiilong/SingletonJob) with the distributed parts removed: where SingletonJob runs a *leader-election loop* next to the job loop, LocalJob runs an *enablement loop*; where SingletonJob gates each iteration on `IsLeader && IsEnabled`, LocalJob gates on `IsEnabled` alone. If you know one library, you know the other.

Two deliberate divergences follow from being in-process only. SingletonJob's `JobName` is a distributed lock-key component, so it must be explicit; LocalJob's is just a log label, so it defaults to the class name. SingletonJob's per-job knobs (`LockExpiry`, `HeartbeatInterval`) are ops concerns configured from outside (`PostConfigureSingletonJob`); LocalJob's per-job knobs describe the job's own behavior, so they live on the class (`ConfigureJobOptions`) and there is no name-keyed registration API at all.

## Concurrency model

- One `BackgroundService` per `LocalBackgroundJob` subclass, registered as `IHostedService` by the source-generated `AddLocalJobs`.
- Inside each service, two loops run in parallel:
  - **enablement loop** — calls `IsJobEnabledAsync` once per `EnabledPollingInterval`, caches the result in `IsEnabled`, and rotates the *enabled term* (see below) on flips;
  - **job loop** — the shape-specific schedule (`ExecuteJobLoopAsync`): interval, fixed-rate, or cron.
- `IsEnabled` is a `bool` field with `Volatile.Read` / `Volatile.Write`. Single writer (enablement loop), N readers (job loop). Eventually-consistent publication is acceptable: a stale read only delays the reaction by one schedule tick.
- If the job loop exits for a non-shutdown reason (invalid interval, escaping exception, a cron with no future occurrences), the enablement loop is cancelled immediately via a linked `CancellationTokenSource`, so the failure is not masked by a still-alive poll loop.

## Cancellation sources

The `CancellationToken` handed to `ExecuteJobAsync` is a link of up to three sources:

```
host shutdown (stoppingToken)          — always
ExecutionTimeout                       — when Options.ExecutionTimeout is set
enabled-term end (live disable)        — when Options.CancelWhenDisabled is true
```

When neither optional source is configured, the raw `stoppingToken` is passed straight through — no linked source is allocated on the hot path.

The three outcomes are logged distinctly:

- **shutdown** — the loop breaks; no error logged.
- **timeout** — swallowed inside the iteration wrapper, logged at `Warning`, schedule continues.
- **disable** — logged at `Information` ("iteration cancelled after the job was disabled"), schedule continues; iterations stay skipped until re-enabled.

## Enabled terms

Mirroring SingletonJob's *leadership terms*: a fresh `CancellationTokenSource` is created whenever the job (re-)enters the enabled state, and cancelled when the live flag flips to disabled. An iteration started during term N holds a link to term N's token, so with `CancelWhenDisabled = true` a disable cancels exactly the in-flight work of that term — a subsequent re-enable starts term N+1 with a fresh source. Term sources are cancelled but never disposed: an in-flight iteration may still hold a linked source over the token, and a CTS without timers needs no disposal.

## Jitter mechanics

`Jitter` exists because N replicas share nothing *except the clock*. Without it, identical pods started by the same deploy run identical schedules at identical instants.

- **interval / fixed-rate**: one uniform draw in `[0, Jitter)` before the schedule starts. Since these schedules are anchored to process start, one draw permanently separates the replicas.

  ```
  replica A  |--jA--|■----------■----------■----------
  replica B  |-jB-|■----------■----------■----------
  replica C  |----jC----|■----------■----------■------
  ```

- **cron**: occurrences are anchored to the wall clock, identical everywhere, so the draw happens before *every* fire:

  ```
  03:00:00 occurrence:   A fires 03:00:01.2   B fires 03:00:03.7   C fires 03:00:00.4
  04:00:00 occurrence:   A fires 04:00:02.9   B fires 04:00:00.1   C fires 04:00:04.2
  ```

The draw uses `Random.Shared`; the delay waits on the job's `TimeProvider`, so jitter is fully controllable in tests via `FakeTimeProvider`.

## Drop-on-overlap (`LocalFixedRateJob`)

`PeriodicTimer.WaitForNextTickAsync` produces ticks at fixed instants. A `volatile bool _isJobRunning` guards `ExecuteJobAsync`. When a tick arrives while a previous run is still in flight, the tick is dropped and logged at `Debug`. On shutdown, the loop awaits the most recent in-flight task before returning, so graceful shutdown is actually graceful.

## Time

Everything that waits or measures goes through `protected TimeProvider TimeProvider`: interval delays, the `PeriodicTimer`, cron sleeps (chunked at one day to stay under `Task.Delay`'s ~49.7-day ceiling and to re-anchor after clock adjustments), jitter delays, `ExecutionTimeout` (via `new CancellationTokenSource(delay, timeProvider)`), iteration duration measurement, and the enablement poll. Construct a job with a `FakeTimeProvider` and a test can drive days of schedule in milliseconds — the test suite does exactly this.

## Startup sequence

```
StartAsync
 ├─ clone IOptions<LocalJobOptions>.Value (each job gets a private copy)
 ├─ apply the class's ConfigureJobOptions override, then re-validate ([Job: name] on failure)
 ├─ duplicate-JobName guard (two classes resolving the same name throw here)
 └─ ExecuteAsync
     ├─ Options.Enabled == false?  → log once, idle forever
     ├─ create initial enabled term
     ├─ start enablement loop
     └─ ExecuteJobLoopAsync (shape)
         ├─ interval:    [startup jitter] → [run if RunOnStartup] → loop { wait → run }
         ├─ fixed-rate:  [startup jitter] → [run if RunOnStartup] → PeriodicTimer loop
         └─ cron:        [jitter+run if RunOnStartup] → loop { sleep to occurrence → jitter → run }
```

## Diagram

```
   ┌──────────┐     ┌──────────┐     ┌──────────┐
   │ Replica  │     │ Replica  │     │ Replica  │
   │   A      │     │   B      │     │   C      │
   │  ■ jobs  │     │  ■ jobs  │     │  ■ jobs  │   ← every replica runs every job
   └──────────┘     └──────────┘     └──────────┘
        no shared state, no election, no backend
        (jitter keeps their schedules apart)
```

Compare SingletonJob, where the three replicas contend for one Redis lock and only the winner runs. The two libraries are complements, not alternatives: most real services have both kinds of work and use both.
