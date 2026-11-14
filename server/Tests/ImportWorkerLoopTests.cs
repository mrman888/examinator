using Examinator.Api;

namespace Examinator.Tests;

public sealed class ImportWorkerLoopTests
{
    [Theory]
    [InlineData("--import-worker")]
    [InlineData("--reconcile")]
    [InlineData("--reconcile", "--poll")]
    public async Task Runs_once_unless_the_import_worker_polls(params string[] args)
    {
        var runs = 0;

        await ImportRegistration.RunWorkerLoopAsync(args, _ => { runs++; return Task.CompletedTask; }, () => 0, TimeSpan.Zero);

        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task Polling_stops_at_the_first_failed_run()
    {
        var runs = 0;

        await ImportRegistration.RunWorkerLoopAsync(["--import-worker", "--poll"],
            _ => { runs++; return Task.CompletedTask; }, () => runs == 3 ? 1 : 0, TimeSpan.Zero);

        Assert.Equal(3, runs);
    }

    [Fact]
    public async Task Polling_does_not_swallow_a_crashed_run()
    {
        var runs = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => ImportRegistration.RunWorkerLoopAsync(
            ["--import-worker", "--poll"], _ => { runs++; throw new InvalidOperationException(); }, () => 0, TimeSpan.Zero));

        Assert.Equal(1, runs);
    }
}
