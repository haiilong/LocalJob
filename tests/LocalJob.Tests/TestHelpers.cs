using FluentAssertions;

namespace LocalJob.Tests;

internal static class TestHelpers
{
    /// <summary>Polls a condition in real time; jobs hop threads after FakeTimeProvider advances, so asserts need a settle window.</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition() && Environment.TickCount64 < deadline)
            await Task.Delay(25);
        condition().Should().BeTrue("condition should be met within {0}ms", timeoutMs);
        // The observed state change and the arming of the job's next fake timer happen in the same async
        // continuation; give it a moment to finish so a subsequent Advance() cannot slip in between.
        await Task.Delay(50);
    }

    /// <summary>Gives in-flight async continuations a moment to settle before asserting a negative.</summary>
    public static Task SettleAsync(int ms = 250) => Task.Delay(ms);
}
