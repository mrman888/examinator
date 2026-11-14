using Examinator.ServiceDefaults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Trace;

namespace Examinator.Tests;

public sealed class ServiceDefaultsTests
{
    [Fact]
    public void Registers_no_telemetry_without_an_exporter_setting()
    {
        var builder = CreateBuilder([]);

        builder.AddServiceDefaults();

        Assert.DoesNotContain(builder.Services, service => service.ServiceType == typeof(TracerProvider));
    }

    [Fact]
    public void Registers_tracing_for_the_otlp_endpoint_set_by_the_app_host()
    {
        var builder = CreateBuilder(new() { [Extensions.OtlpEndpointKey] = "http://localhost:4317" });

        builder.AddServiceDefaults();

        Assert.Contains(builder.Services, service => service.ServiceType == typeof(TracerProvider));
    }

    private static HostApplicationBuilder CreateBuilder(Dictionary<string, string?> settings)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(settings);
        return builder;
    }
}
