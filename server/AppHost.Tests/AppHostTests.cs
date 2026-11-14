using System.Net;
using Aspire.Hosting;
using Aspire.Hosting.Testing;

namespace Examinator.AppHost.Tests;

// Starts the real Api and Web processes through the AppHost. Nothing here reaches Azure: imports are
// off, the Foundry endpoint does not resolve and /api/test is never called.
public sealed class AppHostTests
{
    private static readonly string[] Offline =
    [
        "--environment=Development",
        "Examinator:Frontend=false",
        "Parameters:foundry-endpoint=https://foundry.invalid/",
        "Parameters:foundry-deployment=unused"
    ];

    [Fact]
    public async Task Api_and_gateway_start_healthy_and_the_gateway_reaches_the_api()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.AppHost>(
            [.. Offline, "Examinator:Imports=false"], timeout.Token);
        await using var app = await builder.BuildAsync(timeout.Token);
        await app.StartAsync(timeout.Token);
        await app.ResourceNotifications.WaitForResourceHealthyAsync("api", timeout.Token);
        await app.ResourceNotifications.WaitForResourceHealthyAsync("web", timeout.Token);

        using var api = app.CreateHttpClient("api", "http");
        using var web = app.CreateHttpClient("web", "http");

        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/healthz", timeout.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await web.GetAsync("/healthz", timeout.Token)).StatusCode);
        // This 503 comes from the Api (imports are off), so it proves the gateway reaches it.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await web.GetAsync("/api/imports", timeout.Token)).StatusCode);
    }

    [Fact]
    public async Task Production_storage_account_names_are_refused()
    {
        var exception = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.AppHost>(
            [
                .. Offline, "Examinator:Imports=true", "Parameters:foundry-import-deployment=unused",
                "Parameters:storage-account=exam0123456789abc"
            ]);
            await using var app = await builder.BuildAsync();
        });

        Assert.Contains("production storage naming pattern", exception.ToString());
    }
}
