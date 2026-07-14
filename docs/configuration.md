# Configuration

## Options

| Option | Type | Default | Notes |
|---|---|---|---|
| `Enabled` | `bool` | `true` | Static kill switch, evaluated once at startup. When `false` the job never executes. For live toggling see [Disabling jobs](#disabling-jobs). |
| `RunOnStartup` | `bool?` | `null` | Run one iteration immediately at boot. `null` defers to the shape default: interval `true`, fixed-rate `false`, cron `false`. See [RunOnStartup](#runonstartup). |
| `Jitter` | `TimeSpan` | `00:00:00` | Max random delay used to desynchronize replicas. See [Jitter](#jitter). |
| `ExecutionTimeout` | `TimeSpan?` | `null` | Cancel an iteration that runs longer than this; the schedule continues with the next iteration. `null` = unlimited. |
| `CancelWhenDisabled` | `bool` | `false` | When `true`, the token passed to `ExecuteJobAsync` also fires if the live flag flips to disabled mid-iteration. Default `false`: a started iteration runs to completion. |
| `EnabledPollingInterval` | `TimeSpan` | `00:00:05` | How often `IsJobEnabledAsync` is re-evaluated. Must be positive. |

No option is required — `AddLocalJobs()` with zero configuration is valid.

`appsettings.json`:

```json
{
  "LocalJob": {
    "Jitter": "00:00:02",
    "ExecutionTimeout": "00:01:00",
    "RunOnStartup": false
  }
}
```

Validation is wired through `IValidateOptions<LocalJobOptions>`. A tiny hosted service resolves `IOptions<LocalJobOptions>.Value` at host start, so bad base config throws `OptionsValidationException` before any job iteration runs. Each job's own `ConfigureJobOptions` result is re-validated during that job's `StartAsync`; the error message is prefixed with `[Job: name]` so you can see which class is at fault.

## Job identity: `JobName`

`JobName` defaults to the class name (`GetType().Name`) — most jobs never touch it. It appears in every log line the library writes and feeds the duplicate-name startup guard. Override it for a stable name that survives class renames, or to disambiguate two job classes that share a simple name across namespaces:

```csharp
public override string JobName => "temp-cleanup";
```

## Per-job configuration: `ConfigureJobOptions`

Per-job settings live **on the job class**, not in `Program.cs`. Override `ConfigureJobOptions`; it receives the job's private copy of the options and runs **last**, after `appsettings.json`:

```csharp
public sealed class TempCleanupJob(IOptions<LocalJobOptions> o, ILogger<TempCleanupJob> l)
    : LocalCronJob(o, l)
{
    protected override void ConfigureJobOptions(LocalJobOptions o)
    {
        o.RunOnStartup = true;
        o.ExecutionTimeout = TimeSpan.FromMinutes(5);
    }
    // ...
}
```

Order of application:
1. Defaults from `new LocalJobOptions()`.
2. The `LocalJob` config section (project-wide; ops can tune it per environment without a code change).
3. `ConfigureJobOptions` on the class — the last word.

So the class only sets the values it insists on; everything else stays ops-tunable. Because the hook runs last, anything you set here **cannot** be overridden at deploy time — for values that should stay tunable, leave them unset and rely on the config section. Environment-dependent decisions are fine: the job is a normal DI service, so inject `IHostEnvironment` (or anything else) and consult it inside the hook.

The resolved options are **frozen at `StartAsync`**. No live reload. Redeploy to pick up config changes. (For runtime toggling, see [Disabling jobs](#disabling-jobs).) The instance passed to `ConfigureJobOptions` is a private clone — mutating it never affects other jobs.

## RunOnStartup

Whether a job fires one iteration immediately at boot, before its regular schedule takes over. Three levels, most specific wins:

1. **Shape default** (used when nothing else is set):

   | Shape | Default | Rationale |
   |---|---|---|
   | `LocalIntervalJob` | `true` | Run-then-wait naturally starts with a run. |
   | `LocalFixedRateJob` | `false` | The tick grid starts one period after boot. |
   | `LocalCronJob` | `false` | Cron means "at these wall-clock times", not "and also at deploy time". |

2. **Code-level *soft* default** for one job class — override the virtual; configuration can still override it:

   ```csharp
   protected override bool DefaultRunOnStartup => true;
   ```

3. **Explicit option** — beats the defaults above. Project-wide via `"LocalJob": { "RunOnStartup": ... }`, or pinned per job in the class (beats configuration too, since `ConfigureJobOptions` runs last):

   ```csharp
   protected override void ConfigureJobOptions(LocalJobOptions o) => o.RunOnStartup = true;
   ```

   Rule of thumb: use `DefaultRunOnStartup` when ops should keep the final say, `ConfigureJobOptions` when the class insists.

Semantics per shape when the resolved value is `true`:

- **interval** — unchanged (this is its default): run, then wait.
- **fixed-rate** — one immediate run, then the normal tick grid. The immediate run counts for overlap protection: a tick arriving while it is still in flight is dropped.
- **cron** — one immediate run, then the normal schedule. The startup run draws a jitter delay like any other occurrence.

When `false`, an interval job waits one full interval before its first run.

Mind the fleet: `RunOnStartup = true` on N replicas means N runs at every deploy, roughly simultaneously (a rolling deploy staggers them somewhat). Configure `Jitter` if the job touches anything shared.

## Jitter

Because every replica runs every job, N pods deployed together execute the same schedule in lockstep — and hit any shared resource (database, downstream API) at the same instant, forever. `Jitter` breaks the lockstep. Each replica draws a uniformly random delay in `[0, Jitter)`:

- **interval / fixed-rate** — drawn **once at startup**; the replica's entire schedule is offset by that amount for the lifetime of the process. Different replicas draw different offsets, which is the point.
- **cron** — drawn **fresh before every occurrence** (including a `RunOnStartup` run). Cron fire times are pinned to the wall clock, identical on every replica, so a one-time startup offset would not spread them; a per-occurrence draw does.

Guidelines:

- Keep `Jitter` well below the schedule period. For a cron job, an occurrence that passes while a jitter delay is still pending is treated as a misfire and handled by the [misfire policy](#cron-misfire-policy).
- `Jitter` delays the *start* of work; it never skips work.
- Zero (the default) disables it. There is deliberately no default jitter: whether lockstep matters depends on what the job touches, and silently delaying every user's job would violate least surprise.

## ExecutionTimeout

An upper bound on a single iteration. When the bound is hit, the `CancellationToken` passed to `ExecuteJobAsync` fires, the event logs at `Warning`, and the schedule continues with the next iteration — a runaway iteration no longer wedges the job forever.

```csharp
protected override void ConfigureJobOptions(LocalJobOptions o) => o.ExecutionTimeout = TimeSpan.FromSeconds(5);
```

Cancellation is cooperative: a job that never observes its token is *requested* to stop, not forcibly aborted. Pass the token to everything awaitable inside `ExecuteJobAsync`.

Interaction with the shapes:

- **interval** — the next wait starts when the cancelled iteration actually returns.
- **fixed-rate** — ticks that arrive while the timed-out iteration is still (not yet) honoring its token are dropped, as usual.
- **cron** — the next occurrence is evaluated after the iteration returns; a long overrun becomes a misfire, handled by the policy.

## Disabling jobs

Two mechanisms, layered. The static one wins.

### Static: `Options.Enabled` (evaluated once at startup)

Project level, disabling every job in the deployment:

```json
{
  "LocalJob": { "Enabled": false }
}
```

Job level, on the class (typically behind an environment check):

```csharp
protected override void ConfigureJobOptions(LocalJobOptions o) => o.Enabled = false;
```

A statically disabled job logs one `Information` line at startup and then idles: no polling, no execution. Because options are frozen at `StartAsync`, changing this requires a redeploy.

### Live: override `IsJobEnabledAsync` (re-evaluated every `EnabledPollingInterval`)

For runtime toggling (feature flags, ops kill switches, A/B canaries), inject your flag service into the job and override `IsJobEnabledAsync`:

```csharp
public sealed class MetricsFlushJob(
    IOptions<LocalJobOptions> options,
    ILogger<MetricsFlushJob> logger,
    IFeatureFlags flags)                                          // any DI service you like
    : LocalFixedRateJob(options, logger)
{
    protected override TimeSpan GetJobInterval() => TimeSpan.FromMilliseconds(500);

    protected override async ValueTask<bool> IsJobEnabledAsync(CancellationToken ct)
        => await flags.IsEnabledAsync("jobs-enabled", ct)         // project-level flag
        && await flags.IsEnabledAsync($"job-{JobName}", ct);      // per-job flag

    protected override Task ExecuteJobAsync(CancellationToken ct) { /* ... */ return Task.CompletedTask; }
}
```

Semantics:

- The enablement loop calls `IsJobEnabledAsync` once per `EnabledPollingInterval` (default 5 s), so a flag flip takes effect within one interval. The job loops check the cached `IsEnabled` before each iteration, so no extra load is put on your flag backend by high-frequency jobs.
- The flag is evaluated **per replica**: a canary rollout can disable the job on one pod while the rest keep running. (There is no lock to hand over — every replica decides only for itself.)
- An iteration already in flight when the flag flips is not cancelled by default; set `CancelWhenDisabled = true` to cancel it through its token.
- If your override throws, the error is logged at `Warning` and the previous state is kept, so a flaky flag backend does not flap the job.
- `Options.Enabled = false` short-circuits everything: `IsJobEnabledAsync` is never called.

If you want the same flag logic on every job, put the override in an intermediate base class per shape and derive your jobs from that.

## Cron misfire policy

When a `LocalCronJob` occurrence passes without firing (the previous execution or a pending jitter delay overran the period, the process was suspended, or the clock jumped forward), the job applies its `MisfirePolicy`:

| Policy | Behavior | Use for |
|---|---|---|
| `Skip` (default) | Drop everything missed; resume at the next future occurrence. | Frequent schedules where the next run supersedes the missed one. |
| `FireOnce` | Run one execution immediately to cover all missed occurrences, then resume the schedule. | Hourly or daily jobs where running late beats not running at all. |
| `CatchUp` | Replay every missed occurrence back-to-back. | Each occurrence processes a distinct time bucket and must not be lost. |

```csharp
public sealed class TempCleanupJob : LocalCronJob
{
    protected override CronMisfirePolicy MisfirePolicy => CronMisfirePolicy.FireOnce;
    // ...
}
```

Misfires under `Skip` and `FireOnce` log at `Warning`; under `CatchUp` each replay logs at `Debug` to avoid log storms after a long gap.

Note the scope: each replica evaluates its own schedule, so the policy also applies per replica. An occurrence missed because a *particular* pod was down is caught up (or skipped) by that pod alone when it restarts — the others were never affected.

## Logging levels

| Event | Level |
|---|---|
| Service start, enabled/disabled transitions | `Information` |
| Per-iteration start/end + duration | `Debug` |
| Dropped fixed-rate ticks, jitter delays | `Debug` |
| `ExecutionTimeout` hit | `Warning` |
| Cron misfire (`Skip`/`FireOnce`) | `Warning` |
| Job exception | `Error` |

To silence per-iteration noise (default), keep `Information`. To trace tick timing during incidents, raise `LocalJob` to `Debug`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "LocalJob": "Debug"
    }
  }
}
```
