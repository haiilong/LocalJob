# Deploying to Kubernetes

LocalJob needs nothing from the cluster. There is no Redis to provision and no coordination to configure. This page covers the operational consequences of "every pod runs every job".

## The multiplication table

Whatever a job does, multiply it by `replicas`. Three replicas of an every-30-seconds job means the work happens three times per 30 seconds, once per pod. That is correct for per-pod state (caches, buffers, temp files) and wrong for global work (reports, outbox sweeps), which belongs in [SingletonJob](https://github.com/haiilong/SingletonJob). Scaling out (`kubectl scale --replicas=10`) scales the job load with it; keep that in mind for jobs that touch shared backends.

## Deploys and RunOnStartup

A rolling deploy restarts every pod. Consequences:

- Jobs with `RunOnStartup = true` (and every `LocalIntervalJob`, whose shape default is `true`) run once **per pod, per deploy**. If that run hits a shared resource, a 20-replica rollout is 20 hits, staggered only by the rollout's own pacing.
- Interval and fixed-rate schedules restart from zero at each deploy. If all pods start together (e.g. `maxSurge: 100%`), their schedules are synchronized from that moment on.

Both are what `Jitter` is for:

```yaml
env:
  - name: LocalJob__Jitter
    value: "00:00:05"
```

Each pod draws its own random offset at startup (and per occurrence for cron jobs), so the fleet spreads over the jitter window instead of firing in lockstep. Size the window to what the shared resource tolerates; keep it well below the schedule period.

## Cron jobs across a fleet

`LocalCronJob` occurrences are wall-clock instants, identical on every pod regardless of when the pod started. A daily `0 3 * * *` on 10 replicas is 10 executions at 03:00:00; jitter spreads them across `[03:00:00, 03:00:00 + Jitter)`. If you catch yourself wanting "only one pod at 03:00", that is `SingletonCronJob` from SingletonJob, not a LocalJob use case.

## Graceful shutdown (SIGTERM)

On SIGTERM the host cancels the stopping token; every job loop exits its wait immediately, and an in-flight iteration is cancelled through its token. The fixed-rate shape additionally awaits its most recent in-flight run before returning, so work is not abandoned mid-flush.

- Keep `terminationGracePeriodSeconds` above your slowest iteration (or set an `ExecutionTimeout` below it, so a runaway iteration cannot block shutdown).
- Honor the `CancellationToken` inside `ExecuteJobAsync`. Cancellation is cooperative.

There is nothing to release or hand over on shutdown: no lock, no lease. A killed pod's jobs simply stop; the other pods never notice.

## Liveness of jobs

A job loop that dies from a non-shutdown error (invalid interval, cron with no future occurrences) logs at `Error`/`Warning` and stops, but does not crash the pod. Watch for:

```
Job {JobName} execution failed.            ← recurring: iteration errors (loop continues)
Cron job {JobName} has no future occurrences. Stopping loop.
```

A built-in `IHealthCheck` for wedged loops is on the roadmap; until then, alert on the log lines above or on the absence of the job's own success signals.

## Resource sizing

Idle cost per job is negligible: one timer wait plus one enablement poll (default every 5 s) per job, no allocations on the hot path when neither `ExecutionTimeout` nor `CancelWhenDisabled` is configured. Budget for the work itself, times the replica count.

## Per-pod canaries

`IsJobEnabledAsync` is evaluated on each pod independently. Bridge it to a feature-flag service that can target by pod or instance and you get per-pod canary control of any job: disable it on one pod, watch, then roll on. See [configuration.md](configuration.md#disabling-jobs).
