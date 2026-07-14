# LocalJob samples

Three identical workers, zero infrastructure. Every worker runs every job on its own schedule — that is
the point of LocalJob. Contrast with [SingletonJob](https://github.com/haiilong/SingletonJob), where only
the elected leader would tick.

## Jobs in this sample

| Job               | Type       | Schedule                                    | Extras demonstrated |
|-------------------|------------|---------------------------------------------|---------------------|
| `HeartbeatJob`    | interval   | every 1 second (run, then wait)             | runs on startup (interval default); default `JobName` |
| `MetricsFlushJob` | fixed-rate | every 500 ms (skip if previous in flight)   | `ExecutionTimeout` of 5 s via `ConfigureJobOptions` |
| `TempCleanupJob`  | cron       | `0 3 * * *` UTC (03:00 daily)               | `RunOnStartup = true` via `ConfigureJobOptions`, `MisfirePolicy.FireOnce` |

None of the jobs override `JobName` — the class name is the job name — and `Program.cs` contains nothing but `AddLocalJobs()`: each job configures itself in `ConfigureJobOptions`.

The sample also sets a project-wide `Jitter` of 2 seconds, so the three workers' schedules are visibly
offset from each other instead of firing in lockstep.

## Run with Docker (closest to k8s reality)

```sh
cd samples
docker compose up --build --scale worker=3
```

All three worker containers print job ticks. Watch the `[heartbeat]` timestamps: each container is offset
by its own random jitter draw. Kill any container:

```sh
docker ps
docker kill <container>
```

The other two are completely unaffected — there is nothing to fail over, because nothing is shared.

## Run locally on Windows (no Docker)

```pwsh
.\run-3-instances.ps1
```

Three pwsh windows open, all ticking. No Redis, no database, nothing to install.

## What to look for in logs

```
LocalJob started: HeartbeatJob
LocalJob started: MetricsFlushJob
LocalJob started: TempCleanupJob
[temp-cleanup] cleaning this instance's temp dir at ...   <-- RunOnStartup override fired at boot
[heartbeat] tick at 12:00:01.421                          <-- offset by this instance's jitter draw
[heartbeat] tick at 12:00:02.424
[metrics-flush] flushing local buffer at 12:00:02.157
...
```

With `"LocalJob": "Debug"` logging (the sample default) you also see per-iteration start/complete lines,
dropped fixed-rate ticks, and the jitter delays being applied.
