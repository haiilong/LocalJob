using LocalJob;

var builder = Host.CreateApplicationBuilder(args);

// AOT-safe registration emitted by the bundled source generator. That's the entire setup:
// project-wide settings come from the "LocalJob" config section, and each job class configures
// itself by overriding ConfigureJobOptions (see MetricsFlushJob and TempCleanupJob).
builder.Services.AddLocalJobs(builder.Configuration);

builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss.fff ";
});

await builder.Build().RunAsync();
