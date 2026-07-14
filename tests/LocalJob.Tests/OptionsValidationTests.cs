using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace LocalJob.Tests;

public class OptionsValidationTests
{
    [Fact]
    public void Defaults_are_valid()
    {
        // Unlike SingletonJob (which requires a ProjectName for its Redis lock keys), LocalJob has no
        // required option: AddLocalJobs() with zero configuration must work out of the box.
        Invoking(new LocalJobOptions()).Should().NotThrow();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Non_positive_enabled_polling_interval_throws(int seconds)
    {
        var opts = new LocalJobOptions { EnabledPollingInterval = TimeSpan.FromSeconds(seconds) };
        Invoking(opts).Should().Throw<InvalidOperationException>().WithMessage("*EnabledPollingInterval*");
    }

    [Fact]
    public void Negative_jitter_throws()
    {
        var opts = new LocalJobOptions { Jitter = TimeSpan.FromSeconds(-1) };
        Invoking(opts).Should().Throw<InvalidOperationException>().WithMessage("*Jitter*");
    }

    [Fact]
    public void Jitter_beyond_timer_limit_throws()
    {
        // Task.Delay rejects anything above uint.MaxValue - 1 ms (~49.7 days).
        var opts = new LocalJobOptions { Jitter = TimeSpan.FromDays(50) };
        Invoking(opts).Should().Throw<InvalidOperationException>().WithMessage("*Jitter*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Non_positive_execution_timeout_throws(int seconds)
    {
        var opts = new LocalJobOptions { ExecutionTimeout = TimeSpan.FromSeconds(seconds) };
        Invoking(opts).Should().Throw<InvalidOperationException>().WithMessage("*ExecutionTimeout*");
    }

    [Fact]
    public void Null_execution_timeout_is_valid()
    {
        var opts = new LocalJobOptions { ExecutionTimeout = null };
        Invoking(opts).Should().NotThrow();
    }

    [Fact]
    public void Resolving_IOptionsValue_with_defaults_does_not_throw()
    {
        var services = new ServiceCollection().ConfigureLocalJobOptions(configuration: null);
        using var sp = services.BuildServiceProvider();

        var act = () => sp.GetRequiredService<IOptions<LocalJobOptions>>().Value;
        act.Should().NotThrow();
    }

    [Fact]
    public void Resolving_IOptionsValue_runs_IValidateOptions_and_throws_OptionsValidationException()
    {
        // ConfigureLocalJobOptions wires up an IValidateOptions<LocalJobOptions>. Resolving Value
        // triggers it; a broken default configuration is the surfaced exception path users will hit.
        var services = new ServiceCollection().ConfigureLocalJobOptions(configuration: null);
        services.PostConfigure<LocalJobOptions>(o => o.EnabledPollingInterval = TimeSpan.Zero);
        using var sp = services.BuildServiceProvider();

        var act = () => sp.GetRequiredService<IOptions<LocalJobOptions>>().Value;
        act.Should().Throw<OptionsValidationException>().WithMessage("*EnabledPollingInterval*");
    }

    [Fact]
    public async Task Startup_hosted_service_validates_at_host_start_before_any_job_ticks()
    {
        // The hosted service registered by ConfigureLocalJobOptions resolves IOptions<T>.Value at StartAsync.
        // Failing here means a misconfigured host never reaches the first job iteration.
        var services = new ServiceCollection().ConfigureLocalJobOptions(configuration: null);
        services.PostConfigure<LocalJobOptions>(o => o.Jitter = TimeSpan.FromSeconds(-1));
        using var sp = services.BuildServiceProvider();

        var startup = sp.GetServices<IHostedService>().Single();

        var act = async () => await startup.StartAsync(CancellationToken.None);
        await act.Should().ThrowAsync<OptionsValidationException>().WithMessage("*Jitter*");
    }

    [Fact]
    public async Task Startup_hosted_service_passes_with_defaults()
    {
        var services = new ServiceCollection().ConfigureLocalJobOptions(configuration: null);
        using var sp = services.BuildServiceProvider();

        var startup = sp.GetServices<IHostedService>().Single();

        await startup.StartAsync(CancellationToken.None);
        // no throw
    }

    private static Action Invoking(LocalJobOptions opts) => () =>
    {
        try
        {
            typeof(LocalJobOptions)
                .GetMethod("Validate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(opts, null);
        }
        catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException is not null)
        {
            throw tie.InnerException;
        }
    };
}
