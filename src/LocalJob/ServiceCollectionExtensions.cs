using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace LocalJob;

/// <summary>DI registration helpers.</summary>
/// <remarks>
/// Registration of jobs themselves is performed by the source-generated <c>AddLocalJobs</c> extension
/// method. The helpers in this class configure the options machinery that <c>AddLocalJobs</c> wires up.
/// Per-job configuration lives on the job class itself: override
/// <see cref="LocalBackgroundJob.ConfigureJobOptions"/>, which runs after the configuration below.
/// </remarks>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Binds the <c>"LocalJob"</c> configuration section to <see cref="LocalJobOptions"/>.
    /// </summary>
    /// <remarks>
    /// Called automatically by the source-generated <c>AddLocalJobs</c>. You normally do not need to call
    /// this directly. Also registers an <see cref="IValidateOptions{TOptions}"/> validator plus a small
    /// <see cref="IHostedService"/> that resolves <see cref="IOptions{TOptions}.Value"/> at startup, so
    /// configuration errors throw <see cref="OptionsValidationException"/> the moment the host starts rather
    /// than at the first job tick.
    /// </remarks>
    public static IServiceCollection ConfigureLocalJobOptions(
        this IServiceCollection services,
        IConfiguration? configuration)
    {
        if (configuration is not null)
        {
            var section = configuration.GetSection(LocalJobOptions.SectionName);
            // Use the Action<T> overload (AOT-safe) and bind manually rather than the reflection-based
            // ConfigurationBinder.Bind. Keeps the lib trimming- and NativeAOT-clean.
            services.Configure<LocalJobOptions>(o => BindFromSection(section, o));
        }
        else
        {
            services.AddOptions<LocalJobOptions>();
        }

        // IValidateOptions runs when IOptions<T>.Value is first resolved. Each job triggers it during
        // StartAsync; the hosted service below triggers it at host start so misconfiguration fails before
        // any IHostedService is started. (A job's own ConfigureJobOptions override is re-validated
        // separately by the job after it runs.)
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<LocalJobOptions>, LocalJobOptionsValidator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, LocalJobOptionsValidationStartup>());

        return services;
    }

    private static void BindFromSection(IConfigurationSection section, LocalJobOptions o)
    {
        if (bool.TryParse(section["Enabled"], out var enabled)) o.Enabled = enabled;

        if (bool.TryParse(section["RunOnStartup"], out var runOnStartup)) o.RunOnStartup = runOnStartup;

        if (TimeSpan.TryParse(section["Jitter"], out var jitter)) o.Jitter = jitter;

        if (TimeSpan.TryParse(section["ExecutionTimeout"], out var timeout)) o.ExecutionTimeout = timeout;

        if (bool.TryParse(section["CancelWhenDisabled"], out var cancelWhenDisabled)) o.CancelWhenDisabled = cancelWhenDisabled;

        if (TimeSpan.TryParse(section["EnabledPollingInterval"], out var polling)) o.EnabledPollingInterval = polling;
    }
}

/// <summary>
/// Wraps <see cref="LocalJobOptions"/>.Validate so it participates in the standard
/// <see cref="IValidateOptions{TOptions}"/> pipeline.
/// </summary>
internal sealed class LocalJobOptionsValidator : IValidateOptions<LocalJobOptions>
{
    public ValidateOptionsResult Validate(string? name, LocalJobOptions options)
    {
        try
        {
            options.Validate();
            return ValidateOptionsResult.Success;
        }
        catch (InvalidOperationException ex)
        {
            return ValidateOptionsResult.Fail(ex.Message);
        }
    }
}

/// <summary>
/// Tiny hosted service that resolves <see cref="IOptions{TOptions}.Value"/> on
/// <see cref="IHostedService.StartAsync(CancellationToken)"/>. Touching <c>Value</c> triggers the registered
/// <see cref="IValidateOptions{TOptions}"/> implementations, so misconfiguration surfaces at host startup
/// rather than at the first job iteration.
/// </summary>
/// <remarks>
/// Implemented locally with just <c>Microsoft.Extensions.Hosting.Abstractions</c> so the library does not need
/// to take a dependency on the full <c>Microsoft.Extensions.Hosting</c> package solely for
/// <c>OptionsBuilder.ValidateOnStart()</c>.
/// </remarks>
internal sealed class LocalJobOptionsValidationStartup(IOptions<LocalJobOptions> options) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = options.Value;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
