# Troubleshooting

## "My job runs N times, once per pod"

That is the contract: LocalJob runs on **every** instance. If exactly one instance should run the job, you want [SingletonJob](https://github.com/haiilong/SingletonJob), which has the same API shape but does Redis-backed leader election. The two libraries coexist happily in one host, so register per-instance chores with `AddLocalJobs` and global jobs with `AddSingletonJobs`.

## "All my pods hit the database at the same instant"

Expected for identical replicas on identical schedules. This is what `Jitter` is for:

```json
{ "LocalJob": { "Jitter": "00:00:05" } }
```

Interval and fixed-rate jobs draw one random offset per process; cron jobs draw a fresh one per occurrence. See [configuration.md](configuration.md#jitter).

## "My job is not running at all"

Check it isn't disabled:

1. Statically disabled: `LocalJob:Enabled` is `false` in config, or the class sets `o.Enabled = false` in its `ConfigureJobOptions`. The job logs `Job {JobName} is disabled by configuration` once at startup and then idles.
2. Dynamically disabled: the job overrides `IsJobEnabledAsync` and the flag source returns false. Look for `Job {JobName} is now DISABLED` (`Information`).
3. The loop died at startup: a zero or negative `GetJobInterval()`, or a cron with no future occurrences, stops the loop with an `Error` or `Warning` log line. Search the logs for the job name.
4. It is still waiting for its first slot: fixed-rate and cron jobs do not run at startup by default, and a daily cron fires at the next wall-clock occurrence, which may be hours away. Set `RunOnStartup = true` if you expected a boot run.

## "My job ran at deploy time and I didn't expect it"

`LocalIntervalJob` runs on startup **by default** (run-then-wait starts with a run). Opt out per job, on the class:

```csharp
protected override void ConfigureJobOptions(LocalJobOptions o) => o.RunOnStartup = false;
```

## "An iteration hangs forever"

Set an `ExecutionTimeout`; the iteration's token fires when the bound is hit, a `Warning` is logged, and the schedule continues:

```csharp
protected override void ConfigureJobOptions(LocalJobOptions o) => o.ExecutionTimeout = TimeSpan.FromMinutes(1);
```

Cancellation is cooperative, so the timeout only helps if `ExecuteJobAsync` passes its token to the things it awaits. A truly stuck synchronous call is not aborted, but the `Warning` still fires and tells you where to look.

## "My cron job logs 'missed scheduled time'"

A misfire: the occurrence passed while the previous run (or a pending jitter delay) was still in flight, or the process was suspended. The default policy drops the missed occurrence and resumes at the next future one, which is usually right for frequent schedules. For hourly or daily jobs where running late beats not running, switch the policy:

```csharp
protected override CronMisfirePolicy MisfirePolicy => CronMisfirePolicy.FireOnce;
```

If the warnings are frequent, the job's runtime (or its `Jitter`) is too close to the cron period. See [configuration.md](configuration.md#cron-misfire-policy).

## "Per-iteration logs are missing"

They're at `Debug`. Raise the level for `LocalJob`:

```json
"Logging": { "LogLevel": { "LocalJob": "Debug" } }
```

## "`AddLocalJobs` is not recognized" / "CS1061 ... no definition for `AddLocalJobs`"

The source generator only runs as part of a build, so on a fresh checkout the symbol does not exist yet and the IDE will red-squiggle the call. **Run `dotnet build` once.** The symbol resolves and IntelliSense works from then on.

If the error persists after a clean build, jump to the next section.

## "The source generator did not run on my project"

If you reference LocalJob via NuGet, the generator should run automatically (it's in the package's `analyzers/dotnet/cs` folder). If you reference via `<ProjectReference>`, analyzers do not flow. See [aot.md](aot.md) for the explicit project reference incantation.

To inspect generator output:

```sh
dotnet build -p:EmitCompilerGeneratedFiles=true -p:CompilerGeneratedFilesOutputPath=Generated
```

Look in `Generated/LocalJob.SourceGenerator/.../LocalJobGeneratedRegistration.g.cs`. If the file exists but `AddLocalJobs` is missing or empty, no concrete subclass of `LocalBackgroundJob` was found in the compilation.

## "InvalidOperationException: Duplicate job name"

Two **different** job classes resolved the same `JobName`, so their log lines would be indistinguishable. Since the default name is the class name, this usually means two job classes share a simple name across namespaces (`Billing.CleanupJob` and `Reporting.CleanupJob`). Override `JobName` on one of them. Multiple *instances of the same class* are fine.

## "CS9124: parameter 'logger' is captured into the state of the enclosing type"

You're using a primary constructor on your job and referencing the `logger` parameter inside a method body:

```csharp
public sealed class MyJob(
    IOptions<LocalJobOptions> options,
    ILogger<MyJob> logger)
    : LocalIntervalJob(options, logger)
{
    protected override Task ExecuteJobAsync(CancellationToken ct)
    {
        logger.LogInformation("..."); // CS9124
        return Task.CompletedTask;
    }
}
```

The base class `LocalBackgroundJob` already stores the logger in a `protected ILogger Logger` field. Referencing the primary-constructor `logger` after forwarding it to `base(...)` makes the compiler synthesize a *second* backing field on your type for the same value. Switch to the inherited `Logger`:

```csharp
protected override Task ExecuteJobAsync(CancellationToken ct)
{
    Logger.LogInformation("...");
    return Task.CompletedTask;
}
```

Same applies to `[LoggerMessage]` source-generated logging: pass `Logger` to the generated method, not the constructor parameter:

```csharp
protected override Task ExecuteJobAsync(CancellationToken ct)
{
    LogTick(Logger, DateTimeOffset.Now);
    return Task.CompletedTask;
}

[LoggerMessage(LogLevel.Information, "tick at {Time:HH:mm:ss.fff}")]
static partial void LogTick(ILogger logger, DateTimeOffset time);
```

Treat the `logger` constructor parameter as write-only: forward it to `base(...)` and never touch it again.

## "OptionsValidationException / InvalidOperationException at startup"

A `LocalJobOptions` value is out of range (`EnabledPollingInterval <= 0`, negative `Jitter`, non-positive `ExecutionTimeout`, or anything above ~49.7 days). The message names the offending option; a bad value set by a job's own `ConfigureJobOptions` is prefixed with `[Job: name]`. The check runs at host start on purpose, before any job ticks.

## "I want different settings for one job only"

Override `ConfigureJobOptions` on that job class:

```csharp
protected override void ConfigureJobOptions(LocalJobOptions o)
{
    o.ExecutionTimeout = TimeSpan.FromMinutes(5);
    o.RunOnStartup = false;
}
```

The base configuration from `appsettings.json` is applied first; this hook runs after (and therefore wins). See [configuration.md](configuration.md#per-job-configuration-configurejoboptions).
