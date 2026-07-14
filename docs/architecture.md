# Architecture

`LocalJob` is deliberately the same machine as [SingletonJob](https://github.com/haiilong/SingletonJob) with the distributed parts removed. Where SingletonJob runs a leader-election loop next to the job loop, LocalJob runs an enablement loop. Where SingletonJob gates each iteration on `IsLeader && IsEnabled`, LocalJob gates on `IsEnabled` alone. If you know one library, you know the other.

Two deliberate divergences follow from being in-process only. SingletonJob's `JobName` is part of a distributed lock key, so it must be explicit; LocalJob's is just a log label, so it defaults to the class name. SingletonJob's per-job knobs (`LockExpiry`, `HeartbeatInterval`) are ops concerns configured from outside via `PostConfigureSingletonJob`; LocalJob's per-job knobs describe the job's own behavior, so they live on the class (`ConfigureJobOptions`) and there is no name-keyed registration API at all.

## Concurrency model

One `BackgroundService` per `LocalBackgroundJob` subclass, registered as `IHostedService` by the source-generated `AddLocalJobs`. Inside each service, two loops run in parallel:

- The enablement loop calls `IsJobEnabledAsync` once per `EnabledPollingInterval`, caches the result in `IsEnabled`, and rotates the enabled term (see below) on flips.
- The job loop is the shape-specific schedule (`ExecuteJobLoopAsync`): interval, fixed-rate, or cron.

`IsEnabled` is a `bool` field accessed with `Volatile.Read` and `Volatile.Write`. Single writer (the enablement loop), N readers (the job loop). Eventually-consistent publication is fine here, because a stale read only delays the reaction by one schedule tick.

If the job loop exits for a non-shutdown reason (invalid interval, escaping exception, a cron with no future occurrences), the enablement loop is cancelled immediately via a linked `CancellationTokenSource`. Otherwise a still-alive poll loop would mask the failure until host shutdown.

## Cancellation sources

The `CancellationToken` handed to `ExecuteJobAsync` is a link of up to three sources:

```
host shutdown (stoppingToken)      always
ExecutionTimeout                   when Options.ExecutionTimeout is set
enabled-term end (live disable)    when Options.CancelWhenDisabled is true
```

When neither optional source is configured, the raw `stoppingToken` is passed straight through and no linked source is allocated on the hot path.

The three outcomes are logged distinctly. Shutdown breaks the loop with no error logged. A timeout is swallowed inside the iteration wrapper, logged at `Warning`, and the schedule continues. A disable is logged at `Information` ("iteration cancelled after the job was disabled") and the schedule continues, with iterations skipped until the job is re-enabled.

## Enabled terms

This mirrors SingletonJob's leadership terms. A fresh `CancellationTokenSource` is created whenever the job enters (or re-enters) the enabled state, and cancelled when the live flag flips to disabled. An iteration started during term N holds a link to term N's token, so with `CancelWhenDisabled = true` a disable cancels exactly the in-flight work of that term, and a later re-enable starts term N+1 with a fresh source. Term sources are cancelled but never disposed: an in-flight iteration may still hold a linked source over the token, and a CTS without timers needs no disposal.

## Jitter mechanics

`Jitter` exists because N replicas share nothing except the clock. Identical pods started by the same deploy run identical schedules at identical instants, and whatever they all touch gets hit N times at once.

Interval and fixed-rate schedules are anchored to process start, so one uniform draw in `[0, Jitter)` before the schedule starts permanently separates the replicas:

```
replica A  |--jA--|■----------■----------■----------
replica B  |-jB-|■----------■----------■----------
replica C  |----jC----|■----------■----------■------
```

Cron occurrences are anchored to the wall clock and identical everywhere, so the draw happens before every fire instead:

```
03:00:00 occurrence:   A fires 03:00:01.2   B fires 03:00:03.7   C fires 03:00:00.4
04:00:00 occurrence:   A fires 04:00:02.9   B fires 04:00:00.1   C fires 04:00:04.2
```

The draw uses `Random.Shared`; the delay waits on the job's `TimeProvider`, so jitter is fully controllable in tests via `FakeTimeProvider`.

## Drop-on-overlap (`LocalFixedRateJob`)

`PeriodicTimer.WaitForNextTickAsync` produces ticks at fixed instants. A `volatile bool _isJobRunning` guards `ExecuteJobAsync`. When a tick arrives while a previous run is still in flight, the tick is dropped and logged at `Debug`. On shutdown, the loop awaits the most recent in-flight task before returning, so graceful shutdown is actually graceful.

## Time

Everything that waits or measures goes through `protected TimeProvider TimeProvider`: interval delays, the `PeriodicTimer`, cron sleeps (chunked at one day to stay under `Task.Delay`'s ~49.7-day ceiling and to re-anchor after clock adjustments), jitter delays, `ExecutionTimeout` (via `new CancellationTokenSource(delay, timeProvider)`), iteration duration measurement, and the enablement poll. Construct a job with a `FakeTimeProvider` and a test can drive days of schedule in milliseconds. The test suite does exactly this.

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

Compare SingletonJob, where the three replicas contend for one Redis lock and only the winner runs. The two libraries are complements rather than alternatives; most real services have both kinds of work and use both.
