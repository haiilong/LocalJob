using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LocalJob.Tests;

public class DuplicateJobNameTests
{
    private static IOptions<LocalJobOptions> Opts() =>
        Options.Create(new LocalJobOptions());

    [Fact]
    public async Task Two_different_classes_sharing_a_job_name_throw_at_startup()
    {
        var name = "dup-" + Guid.NewGuid().ToString("N");
        using var jobA = new DuplicateNameJobA(Opts(), NullLogger<DuplicateNameJobA>.Instance, name);
        using var jobB = new DuplicateNameJobB(Opts(), NullLogger<DuplicateNameJobB>.Instance, name);

        await jobA.StartAsync(CancellationToken.None);

        var act = async () => await jobB.StartAsync(CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Duplicate job name*");

        await jobA.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Multiple_instances_of_the_same_class_are_allowed()
    {
        var name = "same-" + Guid.NewGuid().ToString("N");
        using var first = new DuplicateNameJobA(Opts(), NullLogger<DuplicateNameJobA>.Instance, name);
        using var second = new DuplicateNameJobA(Opts(), NullLogger<DuplicateNameJobA>.Instance, name);

        await first.StartAsync(CancellationToken.None);
        await second.StartAsync(CancellationToken.None); // no throw: multi-instance of one class is fine

        await first.StopAsync(CancellationToken.None);
        await second.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Disposing_a_job_releases_its_name_for_a_new_class()
    {
        var name = "released-" + Guid.NewGuid().ToString("N");
        var jobA = new DuplicateNameJobA(Opts(), NullLogger<DuplicateNameJobA>.Instance, name);
        await jobA.StartAsync(CancellationToken.None);
        await jobA.StopAsync(CancellationToken.None);
        jobA.Dispose();

        using var jobB = new DuplicateNameJobB(Opts(), NullLogger<DuplicateNameJobB>.Instance, name);
        await jobB.StartAsync(CancellationToken.None); // no throw: the name was released on Dispose
        await jobB.StopAsync(CancellationToken.None);
    }
}
